// ============================================================================
// Models/EpubModels.cs
// ============================================================================
// Plain data classes ("models") that describe an opened ePub book in memory.
//
// Background — what an ePub actually is:
//   An .epub file is nothing more than a ZIP archive with a fixed internal
//   layout:
//
//     mimetype                      -> must contain "application/epub+zip"
//     META-INF/container.xml        -> points at the OPF "package document"
//     <somewhere>/content.opf       -> the heart of the book:
//         <metadata>  title, author, language, ...
//         <manifest>  every file in the book (chapters, css, images) with an id
//         <spine>     the READING ORDER: an ordered list of manifest ids
//     chapter files (.xhtml)        -> the actual text, ordinary XHTML
//     toc.ncx / nav.xhtml           -> table of contents (chapter titles)
//
//   The parser (Services/EpubParserService.cs) reads those pieces and fills
//   in the classes below. The UI then only talks to these simple objects and
//   never needs to know ZIP/XML details.
// ============================================================================

namespace EslEpubReader.Models;

/// <summary>
/// One chapter (more precisely: one "spine item") of the book, i.e. one
/// XHTML file that should be displayed as a continuous page in the reader.
/// </summary>
public sealed class EpubChapter
{
    /// <summary>
    /// Human-readable title shown in the chapter list of the UI.
    /// Comes from the table of contents (nav.xhtml or toc.ncx) when
    /// available; otherwise the parser generates "Chapter 1", "Chapter 2"...
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Path of the chapter's XHTML file RELATIVE to the folder the ePub was
    /// extracted into, using forward slashes (e.g. "OEBPS/chapter01.xhtml").
    /// The reader turns this into a URL such as
    /// "https://epub.reader.local/OEBPS/chapter01.xhtml" for WebView2.
    /// </summary>
    public required string RelativePath { get; init; }

    /// <summary>0-based position of this chapter in the reading order.</summary>
    public required int SpineIndex { get; init; }

    /// <summary>
    /// ToString() is what a WinUI ListView displays by default when it is
    /// given an object without a DataTemplate — returning the title makes
    /// the chapter list "just work".
    /// </summary>
    public override string ToString() => Title;
}

/// <summary>
/// A fully opened book: metadata + where its files were extracted + the
/// ordered list of chapters.
/// </summary>
public sealed class EpubBook
{
    /// <summary>Book title from &lt;dc:title&gt; (or the file name as fallback).</summary>
    public required string Title { get; init; }

    /// <summary>Author from &lt;dc:creator&gt; ("Unknown author" if absent).</summary>
    public required string Author { get; init; }

    /// <summary>
    /// Absolute path of the temporary folder the ZIP was extracted into.
    /// WebView2 maps this folder to a fake secure origin
    /// (https://epub.reader.local/) so that the chapter's relative links to
    /// CSS files and images keep working exactly as the publisher intended.
    /// </summary>
    public required string ExtractedFolder { get; init; }

    /// <summary>Chapters in reading order (the OPF &lt;spine&gt;).</summary>
    public required IReadOnlyList<EpubChapter> Chapters { get; init; }
}
