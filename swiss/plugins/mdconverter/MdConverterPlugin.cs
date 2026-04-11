using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using utils;

namespace plugins.mdconverter
{
    internal class MdConverterPlugin : Plugin
    {
        public override string Name => "mdconverter";
        public override string Description => "converte un file md in html (default) e pdf";

        private bool InParagraph = false;
        private bool InCodeBlock = false;
        private bool IsFirstCodeLine = false;
        private bool InTable = false;
        private bool InBlockquote = false;
        private bool HeaderWritten = false;
        private int CurrentIndent = 0;
        private Stack<ListInfo> ListStack = new();

        struct ListInfo(string t, int i)
        {
            public string tag = t;
            public int indent = i;
        }

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            if (args.Length < 1)
            {
                Help();
                return;
            }

            // # ---------------------------------- #
            // # 1. Parsing e validazione argomenti #
            // # ---------------------------------- #
            string mdPath = args[0];
            if (mdPath.StartsWith('.'))
            {
                mdPath = Path.Combine(Environment.CurrentDirectory, mdPath[1..]);
            }
            if (!Path.Exists(mdPath))
            {
                PrintError($"il file md \"{mdPath}\" non esiste");
                return;
            }
            var options = ParseArguments(args, 1);
            // booleani
            var convertToPdf = options.ContainsKey("--pdf") || options.ContainsKey("-p");
            var keepHtml = options.ContainsKey("--keephtml") || options.ContainsKey("-k");
            var darkMode = options.ContainsKey("--dark") || options.ContainsKey("-d");
            // chiave valore
            var destPath = options.TryGetValue("--destpath", out var dc) ? dc : options.TryGetValue("-dp", out var ds) ? ds : null;
            if (!string.IsNullOrEmpty(destPath) && !Path.Exists(destPath))
            {
                PrintError($"il percorso di destinazione \"{destPath}\" non esiste");
                return;
            }

            // # ---------------------- #
            // # 2. Conversione file MD #
            // # ---------------------- #
            string htmlFilePath;
            if (!String.IsNullOrEmpty(destPath))
            {
                htmlFilePath = Path.Combine(destPath, Path.GetFileNameWithoutExtension(mdPath));
            }
            else
            {
                htmlFilePath = Path.Combine(Path.GetDirectoryName(mdPath), Path.GetFileNameWithoutExtension(mdPath));
            }
            htmlFilePath += ".html";
            await using var writer = new StreamWriter(
                htmlFilePath,
                Encoding.UTF8,
                new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    BufferSize = 4 * 1024,
                });
            await writer.WriteLineAsync($"<!DOCTYPE html>\n<html lang=\"it\" {(darkMode ? "class=\"dark\"" : "")}>\n<head>\n<meta charset=\"utf-8\">");
            // PrismJs per colorare il codice
            await writer.WriteLineAsync("<script src=\"https://cdnjs.cloudflare.com/ajax/libs/prism/9000.0.1/prism.min.js\" crossorigin=\"anonymous\" referrerpolicy=\"no-referrer\"></script>");
            await writer.WriteLineAsync("<script src=\"https://cdnjs.cloudflare.com/ajax/libs/prism/9000.0.1/components/prism-csharp.min.js\" crossorigin=\"anonymous\" referrerpolicy=\"no-referrer\"></script>");
            // tema del codice
            if (darkMode)
            {
                await writer.WriteLineAsync("<link rel=\"stylesheet\" href=\"https://cdnjs.cloudflare.com/ajax/libs/prism-themes/1.9.0/prism-one-dark.min.css\" crossorigin=\"anonymous\" referrerpolicy=\"no-referrer\"/>");
            }
            else
            {
                await writer.WriteLineAsync("<link rel=\"stylesheet\" href=\"https://cdnjs.cloudflare.com/ajax/libs/prism-themes/1.9.0/prism-one-light.min.css\" crossorigin=\"anonymous\" referrerpolicy=\"no-referrer\" />");
            }
            // Font di google
            await writer.WriteLineAsync("<link rel=\"preconnect\" href=\"https://fonts.googleapis.com\">\r\n<link rel=\"preconnect\" href=\"https://fonts.gstatic.com\" crossorigin>\r\n<link href=\"https://fonts.googleapis.com/css2?family=JetBrains+Mono:ital,wght@0,100..800;1,100..800&family=Roboto:ital,wght@0,100..900;1,100..900&display=swap\" rel=\"stylesheet\">");
            // KateX
            await writer.WriteLineAsync("<link rel=\"stylesheet\" href=\"https://cdn.jsdelivr.net/npm/katex@0.16.45/dist/katex.min.css\" crossorigin=\"anonymous\">");
            await writer.WriteLineAsync("<script defer src=\"https://cdn.jsdelivr.net/npm/katex@0.16.45/dist/katex.min.js\" crossorigin=\"anonymous\"></script>");
            await writer.WriteLineAsync("<script defer src=\"https://cdn.jsdelivr.net/npm/katex@0.16.45/dist/contrib/auto-render.min.js\" crossorigin=\"anonymous\" onload=\"renderMathInElement(document.body, {delimiters: [{left: '$$', right: '$$', display: true},{left: '$', right: '$', display: false}], throwOnError : false});\"></script>");
            // CSS
            await writer.WriteLineAsync("<style>body,pre{font-size:1.1em}:not(pre)>code,code[class*=language-],pre[class*=language-]{font-size:.9em;font-family:\"JetBrains Mono\",monospace!important}ol,p,ul{margin:5px 0}:root{--bc:#fff;--hr:#ddd;--color:#151515;--main:#151515;--code-bc:#f1f1f1;--blockquote-border:#dfe2e5;--blockquote-bc:#f1f1f1;--blockquote-color:#222}html.dark{--bc:#111;--hr:#333;--color:#eee;--main:#eee;--code-bc:#151515;--blockquote-border:#252525;--blockquote-bc:#151515;--blockquote-color:#ddd}*{box-sizing:border-box}body,html{background-color:var(--bc);-webkit-print-color-adjust: exact;print-color-adjust: exact;}body{margin: 0 auto;padding: 10px;font-family:Roboto,Helvetica,sans-serif;line-height:1.6;color:var(--color);max-width:800px}pre{font-family:\"Jetbrains Mono\"!important;tab-size:4;padding:10px;border-radius:10px}:not(pre)>code{background-color:var(--code-bc);padding:2px 5px;border-radius:4px;color:var(--main)}h1{border-bottom:2px solid var(--hr);margin-bottom:.3em}h2{border-bottom:1px solid var(--hr);margin-bottom:.2em}hr{border:none;display:block;background-color:var(--hr);height:2px}ol,ul{padding-inline-start:25px}blockquote{border-left:.25em solid var(--blockquote-border);background-color:var(--blockquote-bc);color:var(--blockquote-color);padding:.5em 1em;margin-left:0}table{border-collapse:collapse;width:100%;margin:15px 0;border:1px solid var(--hr)}td,th{padding:8px 12px;border:1px solid var(--hr);text-align:left}th{background-color:var(--code-bc);font-weight:700}tr:nth-child(even){background-color:rgba(128,128,128,.05)}.math-block{text-align:center}</style>");
            await writer.WriteLineAsync("</head>\n<body>");

            // utility
            // regex per matchare liste non ordinate e ordinate
            var ulRegex = new Regex(@"^(-|\*)\s(.*)", RegexOptions.Compiled | RegexOptions.NonBacktracking);
            var olRegex = new Regex(@"^\d+\.\s(.*)", RegexOptions.Compiled | RegexOptions.NonBacktracking);
            // ciclo principale
            await foreach (var lineExtracted in File.ReadLinesAsync(mdPath, ct))
            {
                CountIndent(lineExtracted);
                var line = lineExtracted.Trim();
                // conto i tab di questa linea
                // # BLOCCHI DI CODICE
                if (line.StartsWith("```"))
                {
                    InCodeBlock = !InCodeBlock;
                    if (InCodeBlock)
                    {
                        CloseTags(writer);
                        var lang = line.Trim()[3..];
                        await writer.WriteAsync($"<pre class=\"language-csharp\"><code class=\"language-{lang}\">");
                        IsFirstCodeLine = true;
                    }
                    else
                    {
                        await writer.WriteAsync("</code></pre>\n");
                    }
                    continue;
                }
                // # Contenuto del blocco di codice
                if (InCodeBlock)
                {
                    if (!IsFirstCodeLine)
                    {
                        await writer.WriteAsync("\n");
                    }
                    await writer.WriteAsync(HttpUtility.HtmlEncode(lineExtracted));
                    IsFirstCodeLine = false;
                    continue;
                }
                // # RIGA VUOTA - chiudo tutti i tag aperti
                if (string.IsNullOrWhiteSpace(line))
                {
                    CloseTags(writer);
                    continue;
                }
                // # DIVISORE
                if (line == "---")
                {
                    CloseTags(writer);
                    await writer.WriteLineAsync("<hr>");
                    continue;
                }
                // # TITOLI
                if (line.StartsWith('#'))
                {
                    CloseTags(writer);
                    int level = line.Length - line.TrimStart('#').Length;
                    string content = ParseInline(line[level..].Trim());
                    await writer.WriteLineAsync($"<h{level}>{content}</h{level}>");
                    continue;
                }
                // # KATEX
                if (line.StartsWith("$$"))
                {
                    CloseTags(writer);
                    await writer.WriteAsync($"<div class=\"math-block\">{line}</div>");
                    continue;
                }
                // # LISTE
                // - non ordinate
                var ulListMatch = ulRegex.Match(line);
                // - ordinate
                var olListMatch = olRegex.Match(line);
                if (ulListMatch.Success || olListMatch.Success)
                {
                    // se cera un paragrafo attivo lo chiudo e apro la lista
                    if (InParagraph) { writer.WriteLine("</p>"); InParagraph = false; }
                    string type = ulListMatch.Success ? "ul" : "ol";
                    string text = ulListMatch.Success ? ulListMatch.Groups[2].Value : olListMatch.Groups[1].Value;
                    text = ParseInline(text);
                    // chiusura di N livelli precedenti se l'indentazione diminuisce
                    while (ListStack.Count > 0 && ListStack.Peek().indent > CurrentIndent)
                    {
                        await writer.WriteLineAsync(ListStack.Pop().tag);
                    }
                    /*
                     * apro la lista se:
                     * - lo stack è vuoto
                     * - indent attuale > last indent dallo stack (esempio: una lista dentro una lista)
                     */
                    if (ListStack.Count == 0 || CurrentIndent > ListStack.Peek().indent)
                    {
                        await writer.WriteLineAsync($"<{type}>");
                        ListStack.Push(new ListInfo($"</{type}>", CurrentIndent));
                    }
                    // ad ogni modo, scrivo l'elemento della lista
                    await writer.WriteLineAsync($"<li>{text}</li>");
                    continue;
                }
                // # TABELLE
                if (line.StartsWith('|'))
                {
                    if (!InTable)
                    {
                        CloseTags(writer);
                        InTable = true;
                        HeaderWritten = false;
                        await writer.WriteLineAsync("<table>");
                    }
                    // Salto la riga di separazione (es: |---|---|)
                    if (line.Contains("---")) continue;
                    var cells = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
                    // Se HeaderWritten è ancora false, questa riga è l'header
                    if (!HeaderWritten)
                    {
                        await writer.WriteLineAsync("<thead><tr>");
                        foreach (var cell in cells)
                        {
                            await writer.WriteLineAsync($"<th>{ParseInline(cell.Trim())}</th>");
                        }
                        await writer.WriteLineAsync("</tr></thead><tbody>");
                        HeaderWritten = true;
                    }
                    else
                    {
                        // Righe normali del body
                        await writer.WriteLineAsync("<tr>");
                        foreach (var cell in cells)
                        {
                            await writer.WriteLineAsync($"<td>{ParseInline(cell.Trim())}</td>");
                        }
                        await writer.WriteLineAsync("</tr>");
                    }
                    continue;
                }
                else if (InTable)
                {
                    await writer.WriteLineAsync("</tbody></table>");
                    InTable = false;
                }
                // # BLOCKQUOTE
                if (line.StartsWith("> "))
                {
                    // se entro nel blockquote per la prima volta apro il tag
                    if (!InBlockquote)
                    {
                        CloseTags(writer);
                        InBlockquote = true;
                        await writer.WriteLineAsync("<blockquote>");
                    }
                    // rimuovo il "> "
                    var content = line.TrimStart('>').Trim();
                    await writer.WriteLineAsync(ParseInline(content));
                    continue;
                }
                else if (InBlockquote)
                {
                    await writer.WriteLineAsync("</blockquote>");
                    InBlockquote = false;
                }
                // # PARAGRAFI
                // se non siamo dentro un paragrafo lo apriamo
                if (!InParagraph)
                {
                    InParagraph = true;
                    await writer.WriteLineAsync("<p>");
                }
                if (InParagraph)
                {
                    await writer.WriteLineAsync(ParseInline(line));
                }
            }
            // terminato il ciclo chiudo tutti i tag se necessario
            CloseTags(writer);
            // chiudo l'HTML
            await writer.WriteLineAsync("</body>\n</html>");
            await writer.DisposeAsync();

            // # --------------------- #
            // # 3. Conversione in PDF #
            // # --------------------- #
            if (convertToPdf) await ConvertToPdf(htmlFilePath, keepHtml, ct);
            else ConsolePlus.Write($"[Cyan]#[Green] HTML generato: [Yellow]{htmlFilePath}[/]");
        }

        private void CountIndent(string line)
        {
            int tabs = 0;
            foreach (char c in line)
            {
                if (c == '\t') tabs++;
                else break;
            }
            CurrentIndent = tabs;
        }

        private void CloseTags(StreamWriter writer)
        {
            if (InParagraph) { writer.WriteLine("</p>"); InParagraph = false; }
            if (InTable) { writer.WriteLine("</tbody></table>"); InTable = false; }
            if (InBlockquote) { writer.WriteLine("</blockquote>"); InBlockquote = false; }
            while (ListStack.Count > 0)
            {
                writer.WriteLine(ListStack.Pop().tag);
            }
            CurrentIndent = -1;
        }

        private string ParseInline(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            StringBuilder sb = new ();
            bool inMath = false;
            bool inBold = false;
            bool inItalic = false;
            bool inCode = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                // gestione escape: se trovo \, salto e scrivo il carattere successivo direttamente
                if (c == '\\' && i + 1 < text.Length && !inMath)
                {
                    sb.Append(text[++i]);
                    continue;
                }
                // 2. codice inline: ` (non processa niente dentro)
                if (c == '`')
                {
                    if (!inCode && !inMath)
                    {
                        sb.Append("<code>");
                        inCode = true;
                    }
                    else if (inCode)
                    {
                        sb.Append("</code>");
                        inCode = false;
                    }
                    else sb.Append(c); // se sono in math scrivo il backtick subito
                    continue;
                }
                // codifico in html caratteri strani se ce ne sono
                if (inCode) { sb.Append(HttpUtility.HtmlEncode(c.ToString())); continue; }
                // matematica: $ (non processo nulla come nel codice)
                if (c == '$')
                {
                    if (!inMath)
                    {
                        sb.Append("<span class=\"math-inline\">$");
                        inMath = true;
                    }
                    else
                    {
                        sb.Append("$</span>");
                        inMath = false;
                    }
                    continue;
                }
                if (inMath) { sb.Append(c); continue; }
                // link
                if (c == '[' && !inMath)
                {
                    int closeBracket = text.IndexOf(']', i);
                    if (closeBracket != -1 && closeBracket + 1 < text.Length && text[closeBracket + 1] == '(')
                    {
                        int closeParen = text.IndexOf(')', closeBracket);
                        if (closeParen != -1)
                        {
                            string label = text.Substring(i + 1, closeBracket - i - 1);
                            string url = text.Substring(closeBracket + 2, closeParen - closeBracket - 2);
                            sb.Append($"<a href=\"{url}\">{ParseInline(label)}</a>");
                            i = closeParen; // Salto alla fine del link
                            continue;
                        }
                    }
                }
                // bold e italic (* o _)
                if (c == '*' || c == '_')
                {
                    // controllo se si tratta di un bold verificando il carattere successivo
                    if (i + 1 < text.Length && text[i + 1] == c)
                    {
                        i++; // schippo il secondo carattere
                        if (!inBold) { sb.Append("<b>"); inBold = true; }
                        else { sb.Append("</b>"); inBold = false; }
                    }
                    else // se è un carattere singolo è italico allora
                    {
                        if (!inItalic) { sb.Append("<i>"); inItalic = true; }
                        else { sb.Append("</i>"); inItalic = false; }
                    }
                    continue;
                }
                // carattere normale
                sb.Append(c);
            }
            // chiudo eventuali tag rimasti aperti
            if (inMath) sb.Append("$</span>");
            if (inBold) sb.Append("</b>");
            if (inItalic) sb.Append("</i>");
            if (inCode) sb.Append("</code>");
            return sb.ToString();
        }

        /// <summary>
        /// Converte il file html generato in un file PDF sfruttando EDGE
        /// </summary>
        /// <param name="htmlFilePath">percorso del file html</param>
        /// <param name="keepHtml">se false elimina il file html dopo aver generato il file pdf</param>
        private async Task<bool> ConvertToPdf(string htmlFilePath, bool keepHtml, CancellationToken ct)
        {
            ConsolePlus.Write("[Cyan]#[/] Conversione in PDF tramite Browser...");
            string pdfFilePath = Path.ChangeExtension(htmlFilePath, ".pdf");
            string edgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
            // TODO: se possibile in futuro aggiungere una ricerca automatica del path del browser
            if (!File.Exists(edgePath))
            {
                PrintError("Eseguibile di Microsoft Edge non trovato. Impossibile generare il PDF.");
                return false;
            }
            // argomenti per la stampa
            string argsStr = $"--headless --print-to-pdf=\"{pdfFilePath}\" --no-pdf-header-footer --virtual-time-budget=2500 --disable-gpu \"file:///{htmlFilePath.Replace('\\', '/')}\"";
            var startInfo = new ProcessStartInfo
            {
                FileName = edgePath,
                Arguments = argsStr,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            try
            {
                using var process = Process.Start(startInfo);
                await process.WaitForExitAsync(ct);
                ConsolePlus.Write($"[Cyan]#[Green] PDF generato: [Yellow]{pdfFilePath}[/]");
            }
            catch (Exception ex)
            {
                PrintError($"Errore durante la conversione PDF: {ex.Message}");
            }

            if (!keepHtml)
            {
                NativeIO.DeleteFile(htmlFilePath);
            }
            return true;
        }

        public override void Help()
        {
            ConsolePlus.Write("[Cyan]#[DarkGray] -------------------------------- [Cyan]#[/]");
            ConsolePlus.Write("[Cyan]#[/] Utilizzo: [Yellow]swiss [Magenta]mdconverter [DarkGray]<percorso> [opzioni]");
            ConsolePlus.Write("[Cyan]#[/] - percorso: percorso del file .md");
            ConsolePlus.Write("[Cyan]#[/] Opzioni:");
            ConsolePlus.Write("[Cyan]#[/] --pdf, -p       : converti in pdf");
            ConsolePlus.Write("[Cyan]#[/] --keephtml, -k  : se converti in pdf e vuoi mantenere l'html");
            ConsolePlus.Write("[Cyan]#[/] --destpath, -dp : path di destinazione del file generato");
            ConsolePlus.Write("[Cyan]#[/] --dark, -d      : genera il documento in dark mode");
            ConsolePlus.Write("[Cyan]#[/] Esempi:");
            ConsolePlus.Write("[Cyan]#[/] - swiss mdconverter .readme.md [DarkGray]-> converte il file readme.md contenuto nella cartella corrente in html[/]");
            ConsolePlus.Write("[Cyan]#[/] - swiss mdconverter C:/folder/readme.md -p -k [DarkGray]-> converte il file readme.md indicato in pdf mantenendo anche il file html[/]");
            ConsolePlus.Write("[Cyan]#[DarkGray] -------------------------------- [Cyan]#[/]");
        }
    }
}
