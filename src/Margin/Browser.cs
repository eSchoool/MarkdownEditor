using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using mshtml;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using WebBrowser = System.Windows.Controls.WebBrowser;

namespace MarkdownEditor
{
    public class Browser : IDisposable
    {
        private string _file;
        private HTMLDocument _htmlDocument;
        private MarkdownPipeline _pipeline;
        private string _htmlTemplate;
        private int _zoomFactor;
        private double _cachedPosition = 0,
                       _cachedHeight = 0,
                       _positionPercentage = 0;

        private List<Block> _markdownBlocks;
        private int _currentViewLine;

        [ThreadStatic]
        private static StringWriter htmlWriterStatic;

        public Browser(string file)
        {
            var builder = new MarkdownPipelineBuilder()
                .UsePragmaLines()
                .UseAdvancedExtensions();

            _pipeline = builder.Build();
            _zoomFactor = GetZoomFactor();
            _file = file;
            _htmlTemplate = GetHtmlTemplate();
            _currentViewLine = -1;

            InitBrowser();
        }

        public WebBrowser Control { get; private set; }

        private void InitBrowser()
        {
            Control = new WebBrowser();
            Control.HorizontalAlignment = HorizontalAlignment.Stretch;

            Control.LoadCompleted += (s, e) =>
            {
                Zoom(_zoomFactor);
                _htmlDocument = (HTMLDocument)Control.Document;

                _cachedHeight = _htmlDocument.body.offsetHeight;
                _htmlDocument.documentElement.setAttribute("scrollTop", _positionPercentage * _cachedHeight / 100);

                foreach (IHTMLElement link in _htmlDocument.links)
                {
                    HTMLAnchorElement anchor = link as HTMLAnchorElement;
                    if (anchor == null || anchor.protocol != "file:")
                        continue;

                    HTMLAnchorEvents_Event handler = anchor as HTMLAnchorEvents_Event;
                    if (handler == null)
                        continue;

                    string file = anchor.pathname.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                    if (!File.Exists(file))
                    {
                        anchor.title = "The file does not exist";
                        return;
                    }

                    handler.onclick += () =>
                    {
                        ProjectHelpers.OpenFileInPreviewTab(file);
                        return true;
                    };
                }
            };

            // Open external links in default browser
            Control.Navigating += (s, e) =>
            {
                if (e.Uri == null)
                    return;

                e.Cancel = true;
                if (e.Uri.IsAbsoluteUri && e.Uri.Scheme.StartsWith("http"))
                    Process.Start(e.Uri.ToString());
            };
        }

        private static int GetZoomFactor()
        {
            using (Graphics g = Graphics.FromHwnd(Process.GetCurrentProcess().MainWindowHandle))
            {
                var baseLine = 96;
                var dpi = g.DpiX;

                if (baseLine == dpi)
                    return 100;

                // 150% scaling => 225
                // 250% scaling => 400

                double scale = dpi * ((dpi - baseLine) / baseLine + 1);
                return Convert.ToInt32(Math.Ceiling(scale / 25)) * 25; // round up to nearest 25
            }
        }

        private int FindClosestLine(int line)
        {
            // Forces the preview window to scroll to the top of the document
            if (line <= 3)
                return 1;

            var elements = _markdownBlocks;
            var lowerIndex = 0;
            var upperIndex = elements.Count - 1;

            // binary search on lines
            while (lowerIndex <= upperIndex)
            {
                int midIndex = (upperIndex - lowerIndex) / 2 + lowerIndex;
                int comparison = elements[midIndex].Line.CompareTo(line);
                if (comparison == 0)
                {
                    return line;
                }
                if (comparison < 0)
                    lowerIndex = midIndex + 1;
                else
                    upperIndex = midIndex - 1;
            }

            // If we are between two lines, try to find the best spot
            if (lowerIndex >= 0 && lowerIndex < elements.Count)
            {
                // we calculate the position of the current line relative to the line found and previous line
                var previousLineIndex = lowerIndex > 0 ? elements[lowerIndex - 1].Line : 0;
                var nextLineIndex = elements[lowerIndex].Line;
                var middle = (line - previousLineIndex) * 1.0 / (nextLineIndex - previousLineIndex);
                // If  relative position < 0.5, we select the previous line, otherwise we select the line found
                return middle < 0.5 ? previousLineIndex : nextLineIndex;
            }

            return 0;
        }

        public void UpdatePosition(int line)
        {
            if (_htmlDocument != null && MarkdownEditorPackage.Options.EnablePreviewSyncNavigation)
            {
                _currentViewLine = FindClosestLine(line);
                SyncNavigation();
            }
        }

        private void SyncNavigation()
        {
            if (MarkdownEditorPackage.Options.EnablePreviewSyncNavigation)
            {
                if (_currentViewLine == 1)
                {
                    // Forces the preview window to scroll to the top of the document
                    _htmlDocument.documentElement.setAttribute("scrollTop", 0);
                }
                else if (_currentViewLine >= 0)
                {
                    var element = _htmlDocument.getElementById("pragma-line-" + _currentViewLine);
                    if (element != null)
                    {
                        element.scrollIntoView(true);
                    }
                }
            }
            else
            {
                _currentViewLine = -1;
                _cachedPosition = _htmlDocument.documentElement.getAttribute("scrollTop");
                _cachedHeight = Math.Max(1.0, _htmlDocument.body.offsetHeight);
                _positionPercentage = _cachedPosition * 100 / _cachedHeight;
            }
        }

        public void UpdateBrowser(string markdown)
        {
            // Generate the HTML document
            string html = null;
            StringWriter htmlWriter = null;
            try
            {
                var doc = Markdown.Parse(markdown, _pipeline);

                htmlWriter = htmlWriterStatic ?? (htmlWriterStatic = new StringWriter());
                htmlWriter.GetStringBuilder().Clear();
                var htmlRenderer = new HtmlRenderer(htmlWriter);
                _pipeline.Setup(htmlRenderer);
                htmlRenderer.Render(doc);
                htmlWriter.Flush();
                html = htmlWriter.ToString();

                // TODO: use a pool for List<Block>
                // Collect all blocks for sync navigation
                var blocks = new List<Block>();
                // This is used by live sync navigation, but we always generate them so that
                // we can enable/disable the sync navigation and it will work correctly
                DumpBlocks(doc, blocks);
                _markdownBlocks = blocks;
            }
            catch (Exception ex)
            {
                // We could output this to the exception pane of VS?
                // Though, it's easier to output it directly to the browser
                html = "<p>An unexpected exception occured:</p><pre>" +
                       ex.ToString().Replace("<", "&lt;").Replace("&", "&amp;") + "</pre>";
            }
            finally
            {
                // Free any resources allocated by HtmlWriter
                htmlWriter?.GetStringBuilder().Clear();
            }

            if (_htmlDocument != null)
            {
                var content = _htmlDocument.getElementById("___markdown-content___");
                content.innerHTML = html;
            }
            else
            {
                var template = string.Format(CultureInfo.InvariantCulture, _htmlTemplate, html);
                Control.NavigateToString(template);
            }

            SyncNavigation();
        }

        private void DumpBlocks(Block block, List<Block> blocks)
        {
            blocks.Add(block);
            var container = block as ContainerBlock;
            if (container != null)
            {
                foreach (var subBlock in container)
                {
                    DumpBlocks(subBlock, blocks);
                }
            }
        }

        private static string GetFolder()
        {
            string assembly = Assembly.GetExecutingAssembly().Location;
            string folder = Path.GetDirectoryName(assembly);
            return folder;
        }

        private string GetHtmlTemplate()
        {
            var baseHref = Path.GetDirectoryName(_file).Replace("\\", "/");
            string folder = GetFolder();
            string cssPath = Path.Combine(folder, "margin\\highlight.css");
            string scriptPath = Path.Combine(folder, "margin\\prism.js");

            return $@"<!DOCTYPE html>
<html lang=""en"">
    <head>
        <meta http-equiv=""X-UA-Compatible"" content=""IE=Edge"" />
        <meta charset=""utf-8"" />
        <base href=""file:///{baseHref}/"" />
        <title>Markdown Preview</title>
        <link rel=""stylesheet"" href=""{cssPath}"" />
</head>
    <body class=""markdown-body"">
        <div id='___markdown-content___'>
          {{0}}
        </div>
        <script src=""{scriptPath}""></script>
    </body>
</html>";
        }

        private void Zoom(int zoomFactor)
        {
            if (zoomFactor == 100)
                return;

            dynamic OLECMDEXECOPT_DODEFAULT = 0;
            dynamic OLECMDID_OPTICAL_ZOOM = 63;
            FieldInfo fiComWebBrowser = typeof(WebBrowser).GetField("_axIWebBrowser2", BindingFlags.Instance | BindingFlags.NonPublic);

            if (fiComWebBrowser == null)
                return;

            object objComWebBrowser = fiComWebBrowser.GetValue(Control);

            if (objComWebBrowser == null)
                return;

            objComWebBrowser.GetType().InvokeMember("ExecWB", BindingFlags.InvokeMethod, null, objComWebBrowser, new object[] {
                OLECMDID_OPTICAL_ZOOM,
                OLECMDEXECOPT_DODEFAULT,
                zoomFactor,
                IntPtr.Zero
            });
        }

        public void Dispose()
        {
            if (Control != null)
                Control.Dispose();

            _htmlDocument = null;
            _pipeline = null;
        }
    }
}
