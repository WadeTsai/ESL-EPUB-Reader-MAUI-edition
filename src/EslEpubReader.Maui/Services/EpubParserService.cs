// ============================================================================
// Services/EpubParserService.cs
// ============================================================================
// Opens an .epub file and turns it into an EpubBook model.
//
// The whole parser is deliberately dependency-free: it uses only
// System.IO.Compression (ZIP) and System.Xml.Linq (XML), so the app has no
// third-party ePub library to keep updated.
//
// PARSING PIPELINE (each step has a matching private method below):
//
//   (1) Extract the ZIP to a per-book temp folder.
//         WebView2 will later serve chapters straight from this folder via a
//         virtual https:// origin, so relative CSS/image links keep working.
//
//   (2) Read META-INF/container.xml.
//         Its <rootfile full-path="..."> attribute tells us WHERE the OPF
//         package document lives (the location varies between publishers:
//         "OEBPS/content.opf", "EPUB/package.opf", ...).
//
//   (3) Read the OPF package document.
//         <metadata> -> title / author
//         <manifest> -> id -> file href map (all files of the book)
//         <spine>    -> ordered list of manifest ids = the READING ORDER
//
//   (4) Read the table of contents to get human chapter titles.
//         EPUB 3 books have a "nav" XHTML document (manifest item with
//         properties="nav"); EPUB 2 books have toc.ncx. We try both and
//         fall back to "Chapter N" when a spine file has no TOC entry.
//
// SECURITY NOTE: step (1) guards against the classic "zip slip" attack where
// a malicious archive contains entries like "..\..\evil.exe" that would
// escape the extraction folder.
// ============================================================================

using System.IO.Compression;
using System.Xml.Linq;
using EslEpubReader.Models;

namespace EslEpubReader.Services;

public sealed class EpubParserService
{
    // XML namespaces used inside ePub files. XElement lookups must be
    // namespace-qualified or they silently find nothing.
    private static readonly XNamespace NsContainer = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace NsOpf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace NsDc = "http://purl.org/dc/elements/1.1/";   // Dublin Core metadata
    private static readonly XNamespace NsNcx = "http://www.daisy.org/z3986/2005/ncx/";
    private static readonly XNamespace NsXhtml = "http://www.w3.org/1999/xhtml";

    /// <summary>
    /// Parse the given .epub file. Runs the heavy work (ZIP extraction, XML
    /// parsing) on a background thread via Task.Run so the UI never freezes,
    /// even for large books.
    /// </summary>
    /// <param name="epubFilePath">Absolute path of the .epub the user picked.</param>
    /// <returns>A fully populated EpubBook model.</returns>
    /// <exception cref="InvalidDataException">The file is not a valid ePub.</exception>
    public Task<EpubBook> ParseAsync(string epubFilePath) =>
        Task.Run(() => ParseCore(epubFilePath));

    /// <summary>Synchronous implementation — see class comment for the pipeline.</summary>
    private static EpubBook ParseCore(string epubFilePath)
    {
        // ---- (1) Extract the ZIP into a unique temp folder. ----------------
        // A fresh GUID-named folder per open avoids collisions when the user
        // opens the same book twice or two books with identical file names.
        string extractRoot = Path.Combine(
            Path.GetTempPath(), "EslEpubReader", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractRoot);

        using (ZipArchive zip = ZipFile.OpenRead(epubFilePath))
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                // Entries ending in '/' are directory markers — skip them,
                // Directory.CreateDirectory below creates folders as needed.
                if (string.IsNullOrEmpty(entry.Name)) continue;

                // Compute the destination path, then verify it is still
                // INSIDE extractRoot ("zip slip" defense): a malicious entry
                // name like "../../x" would otherwise write outside our folder.
                string destination = Path.GetFullPath(Path.Combine(extractRoot, entry.FullName));
                if (!destination.StartsWith(extractRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"ePub contains an unsafe path and was rejected: {entry.FullName}");

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
        }

        // ---- (2) container.xml -> location of the OPF package document. ---
        string containerPath = Path.Combine(extractRoot, "META-INF", "container.xml");
        if (!File.Exists(containerPath))
            throw new InvalidDataException("Not a valid ePub: META-INF/container.xml is missing.");

        XDocument containerXml = XDocument.Load(containerPath);
        // <rootfiles><rootfile full-path="OEBPS/content.opf" .../></rootfiles>
        string? opfRelative = containerXml
            .Descendants(NsContainer + "rootfile")
            .Select(rf => rf.Attribute("full-path")?.Value)
            .FirstOrDefault(p => !string.IsNullOrEmpty(p));
        if (opfRelative is null)
            throw new InvalidDataException("Not a valid ePub: container.xml has no <rootfile>.");

        // The path inside the ZIP uses '/', convert for the local filesystem.
        string opfFullPath = Path.Combine(extractRoot, opfRelative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(opfFullPath))
            throw new InvalidDataException($"Not a valid ePub: package document '{opfRelative}' is missing.");

        // All hrefs inside the OPF are RELATIVE TO THE OPF's OWN FOLDER,
        // so remember that folder (e.g. "OEBPS") for later path resolution.
        string opfDirRelative = Path.GetDirectoryName(opfRelative.Replace('/', Path.DirectorySeparatorChar)) ?? "";

        // ---- (3) OPF: metadata + manifest + spine. -------------------------
        XDocument opf = XDocument.Load(opfFullPath);

        // Title / author. Descendants() search tolerates the different
        // nesting some publishers use. Fall back to the file name / a stub.
        string title = opf.Descendants(NsDc + "title").FirstOrDefault()?.Value.Trim()
                       is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(epubFilePath);
        string author = opf.Descendants(NsDc + "creator").FirstOrDefault()?.Value.Trim()
                        is { Length: > 0 } a ? a : "Unknown author";

        // Manifest: build id -> (href, mediaType, properties) lookup tables.
        var manifestHrefById = new Dictionary<string, string>(StringComparer.Ordinal);
        string? navDocHref = null;   // EPUB 3 TOC (properties="nav")
        string? ncxHref = null;      // EPUB 2 TOC (media-type NCX)
        foreach (XElement item in opf.Descendants(NsOpf + "item"))
        {
            string? id = item.Attribute("id")?.Value;
            string? href = item.Attribute("href")?.Value;
            if (id is null || href is null) continue;

            // Chapter hrefs may be URL-encoded ("My%20Chapter.xhtml").
            href = Uri.UnescapeDataString(href);
            manifestHrefById[id] = href;

            // properties="nav" marks the EPUB 3 navigation document.
            string properties = item.Attribute("properties")?.Value ?? "";
            if (properties.Split(' ').Contains("nav")) navDocHref = href;

            // The NCX is identified by its media type.
            if (item.Attribute("media-type")?.Value == "application/x-dtbncx+xml") ncxHref = href;
        }

        // Spine: ordered idrefs -> ordered chapter hrefs (the reading order).
        // linear="no" items are auxiliary content (e.g. answer keys) that
        // should not appear in the main flow, so we skip them.
        List<string> spineHrefs = opf.Descendants(NsOpf + "itemref")
            .Where(ir => ir.Attribute("linear")?.Value != "no")
            .Select(ir => ir.Attribute("idref")?.Value)
            .Where(idref => idref is not null && manifestHrefById.ContainsKey(idref))
            .Select(idref => manifestHrefById[idref!])
            .ToList();

        if (spineHrefs.Count == 0)
            throw new InvalidDataException("Not a valid ePub: the spine lists no readable chapters.");

        // ---- (4) Table of contents -> chapter titles. ----------------------
        // Map: chapter href (WITHOUT #fragment, relative to OPF dir) -> title.
        Dictionary<string, string> titleByHref = LoadTocTitles(extractRoot, opfDirRelative, navDocHref, ncxHref);

        // ---- Assemble the final chapter list. ------------------------------
        var chapters = new List<EpubChapter>(spineHrefs.Count);
        for (int i = 0; i < spineHrefs.Count; i++)
        {
            string href = spineHrefs[i];

            // Path relative to the EXTRACTION ROOT (what WebView2 needs),
            // normalized to forward slashes for use in a URL.
            string relativeToRoot = string.IsNullOrEmpty(opfDirRelative)
                ? href
                : $"{opfDirRelative.Replace(Path.DirectorySeparatorChar, '/')}/{href}";

            // Prefer the TOC title; otherwise synthesize "Chapter N".
            string chapterTitle = titleByHref.TryGetValue(href, out string? tocTitle)
                ? tocTitle
                : $"Chapter {i + 1}";

            chapters.Add(new EpubChapter
            {
                Title = chapterTitle,
                RelativePath = relativeToRoot,
                SpineIndex = i,
            });
        }

        return new EpubBook
        {
            Title = title,
            Author = author,
            ExtractedFolder = extractRoot,
            Chapters = chapters,
        };
    }

    /// <summary>
    /// Read chapter titles from the table of contents.
    /// Tries the EPUB 3 nav document first (it is the modern standard),
    /// then the EPUB 2 NCX. Returns an empty map if neither exists —
    /// callers must fall back to generated titles.
    /// </summary>
    /// <param name="extractRoot">Folder the ePub was extracted into.</param>
    /// <param name="opfDirRelative">OPF folder relative to extractRoot.</param>
    /// <param name="navDocHref">href of the EPUB 3 nav doc (relative to OPF dir), or null.</param>
    /// <param name="ncxHref">href of the EPUB 2 NCX (relative to OPF dir), or null.</param>
    private static Dictionary<string, string> LoadTocTitles(
        string extractRoot, string opfDirRelative, string? navDocHref, string? ncxHref)
    {
        // Keys are chapter hrefs relative to the OPF dir — the same form the
        // spine uses — so the caller can match them up directly.
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Helper: turn an OPF-relative href into an absolute file path.
        string ToFullPath(string href) => Path.Combine(
            extractRoot, opfDirRelative, href.Replace('/', Path.DirectorySeparatorChar));

        // Helper: TOC links may point at anchors ("ch1.xhtml#s2") — the
        // chapter FILE is the part before '#'.
        static string StripFragment(string href)
        {
            int hash = href.IndexOf('#');
            return hash >= 0 ? href[..hash] : href;
        }

        try
        {
            // ---- EPUB 3: <nav epub:type="toc"> with nested <a href> links. --
            if (navDocHref is not null && File.Exists(ToFullPath(navDocHref)))
            {
                XDocument nav = XDocument.Load(ToFullPath(navDocHref));

                // The nav doc's links are relative to the NAV DOC's folder,
                // which may differ from the OPF folder — rebase them.
                string navDir = Path.GetDirectoryName(navDocHref.Replace('/', '\\'))?.Replace('\\', '/') ?? "";

                foreach (XElement a in nav.Descendants(NsXhtml + "a"))
                {
                    string? href = a.Attribute("href")?.Value;
                    string text = a.Value.Trim();
                    if (href is null || text.Length == 0) continue;

                    string file = StripFragment(Uri.UnescapeDataString(href));
                    string rebased = string.IsNullOrEmpty(navDir) ? file : $"{navDir}/{file}";
                    titles.TryAdd(rebased, text);   // first entry wins (usually the chapter head)
                }

                if (titles.Count > 0) return titles;
            }

            // ---- EPUB 2: NCX <navPoint><navLabel><text> + <content src>. ---
            if (ncxHref is not null && File.Exists(ToFullPath(ncxHref)))
            {
                XDocument ncx = XDocument.Load(ToFullPath(ncxHref));
                string ncxDir = Path.GetDirectoryName(ncxHref.Replace('/', '\\'))?.Replace('\\', '/') ?? "";

                foreach (XElement navPoint in ncx.Descendants(NsNcx + "navPoint"))
                {
                    string? src = navPoint.Element(NsNcx + "content")?.Attribute("src")?.Value;
                    string? label = navPoint.Element(NsNcx + "navLabel")?.Element(NsNcx + "text")?.Value.Trim();
                    if (src is null || string.IsNullOrEmpty(label)) continue;

                    string file = StripFragment(Uri.UnescapeDataString(src));
                    string rebased = string.IsNullOrEmpty(ncxDir) ? file : $"{ncxDir}/{file}";
                    titles.TryAdd(rebased, label);
                }
            }
        }
        catch
        {
            // A broken TOC must never prevent the book from opening — the
            // reader simply shows generated "Chapter N" names instead.
        }

        return titles;
    }

    /// <summary>
    /// Best-effort cleanup of a previously extracted book folder, called when
    /// a new book replaces the old one. Failures are ignored: temp folders
    /// are also cleaned by Windows storage sense / disk cleanup eventually.
    /// </summary>
    public static void TryCleanupExtractedFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        try { Directory.Delete(folder, recursive: true); }
        catch { /* file may be locked by WebView2 — safe to ignore */ }
    }
}
