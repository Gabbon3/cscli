using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using lib.io;
using lib.console;

namespace plugins.mdconverter
{
    internal class MdConverterPlugin : Plugin
    {
        public override string Name => "mdconverter";
        public override string Description => "converte un file md in html (default) e pdf";

        private bool InParagraph = false;
        private bool InCodeBlock = false;
        private bool InMathBlock = false;
        private bool IsFirstCodeLine = false;
        private bool IsMermaid = false;
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
            var settings = ParseSettings<MdConverterSettings>(args);
            if (args.Contains("--help") || string.IsNullOrEmpty(settings.TargetPath))
            {
                Help();
                return;
            }

            // # ---------------------------------- #
            // # 1. Parsing e validazione argomenti #
            // # ---------------------------------- #
            string mdPath = Path.GetFullPath(settings.TargetPath);
            if (!Path.Exists(mdPath))
            {
                PrintError($"Il file md \"{mdPath}\" non esiste");
                return;
            }
            // booleani
            bool convertToPdf = settings.Pdf;
            bool keepHtml = settings.KeepHtml;
            bool darkMode = settings.DarkMode;
            // chiave valore
            string? destPath = settings.DestPath;
            if (!string.IsNullOrEmpty(destPath))
            {
                destPath = Path.GetFullPath(destPath);
                if (!Directory.Exists(destPath))
                {
                    PrintError($"Il percorso di destinazione \"{destPath}\" non esiste");
                    return;
                }
            }
            // opzioni mermaid
            // - tema
            string mermaidTheme = settings.MermaidTheme ?? (darkMode ? "dark" : "default");

            // # ---------------------- #
            // # 2. Conversione file MD #
            // # ---------------------- #
            string htmlFilePath;
            if (!string.IsNullOrEmpty(destPath))
            {
                htmlFilePath = Path.Combine(destPath, Path.GetFileNameWithoutExtension(mdPath));
            }
            else
            {
                htmlFilePath = Path.Combine(Path.GetDirectoryName(mdPath) ?? "", Path.GetFileNameWithoutExtension(mdPath));
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
            await writer.WriteLineAsync("<script src=\"https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/prism.min.js\"></script>");
            // - Autoloader per multilinguaggio
            await writer.WriteLineAsync("<script src=\"https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/plugins/autoloader/prism-autoloader.min.js\"></script>");
            // - Linenumbers
            await writer.WriteLineAsync("<script src=\"https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/plugins/line-numbers/prism-line-numbers.min.js\"></script>");
            await writer.WriteLineAsync("<link rel=\"stylesheet\" href=\"https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/plugins/line-numbers/prism-line-numbers.min.css\" />");
            // tema del codice
            if (darkMode)
            {
                await writer.WriteLineAsync("<link rel=\"stylesheet\" href=\"https://cdnjs.cloudflare.com/ajax/libs/prism-themes/1.9.0/prism-one-dark.min.css\" />");
            }
            else
            {
                await writer.WriteLineAsync("<link rel=\"stylesheet\" href=\"https://cdnjs.cloudflare.com/ajax/libs/prism-themes/1.9.0/prism-one-light.min.css\" />");
            }
            // Font di google
            await writer.WriteLineAsync("<link rel=\"preconnect\" href=\"https://fonts.googleapis.com\">\r\n<link rel=\"preconnect\" href=\"https://fonts.gstatic.com\" crossorigin>\r\n<link href=\"https://fonts.googleapis.com/css2?family=JetBrains+Mono:ital,wght@0,100..800;1,100..800&family=Roboto:ital,wght@0,100..900;1,100..900&display=swap\" rel=\"stylesheet\">");
            // KateX
            await writer.WriteLineAsync("<link rel=\"stylesheet\" href=\"https://cdn.jsdelivr.net/npm/katex@0.16.45/dist/katex.min.css\" crossorigin=\"anonymous\">");
            await writer.WriteLineAsync("<script defer src=\"https://cdn.jsdelivr.net/npm/katex@0.16.45/dist/katex.min.js\" crossorigin=\"anonymous\"></script>");
            await writer.WriteLineAsync("<script defer src=\"https://cdn.jsdelivr.net/npm/katex@0.16.45/dist/contrib/auto-render.min.js\" crossorigin=\"anonymous\" onload=\"renderMathInElement(document.body, {delimiters: [{left: '$$', right: '$$', display: true},{left: '$', right: '$', display: false}], throwOnError : false});\"></script>");
            // Google Icons
            await writer.WriteLineAsync("<link rel=\"stylesheet\" href=\"https://fonts.googleapis.com/css2?family=Material+Symbols+Rounded:opsz,wght,FILL,GRAD@24,300,0,0\" />");
            // MermaidJs - grafici tramite codice
            await writer.WriteLineAsync("<script src=\"https://cdnjs.cloudflare.com/ajax/libs/mermaid/11.12.0/mermaid.min.js\"></script>");
            await writer.WriteLineAsync($@"<script>
    mermaid.initialize({{
        startOnLoad: true,
        theme: '{mermaidTheme}',
        securityLevel: 'loose',
        themeVariables: {{ fontFamily: 'Roboto, sans-serif' }}
    }});
</script>");
            // CSS e apertura body
            await writer.WriteLineAsync($"<style>{GetEmbeddedCss()}</style>\n</head>\n<body class='line-numbers'>");

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
                        // se è un grafico mermaid
                        if (lang == "mermaid")
                        {
                            await writer.WriteAsync("<div class=\"mermaid\">");
                            IsMermaid = true;
                        }
                        else // è un blocco codice qualsiasi
                        {
                            await writer.WriteAsync($"<pre><code class=\"language-{lang}\">");
                        }
                        IsFirstCodeLine = true;
                    }
                    else
                    {
                        if (IsMermaid)
                        {
                            await writer.WriteLineAsync("</div>");
                            IsMermaid = false;
                        }
                        else
                        {
                            await writer.WriteAsync("</code></pre>\n");
                        }
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
                // # MATH BLOCK
                // se sono gia dentro un blocco matematico
                if (InMathBlock)
                {
                    // verifico se devo chiuderlo
                    if (line.EndsWith("$$"))
                    {
                        InMathBlock = false;
                        await writer.WriteLineAsync($"{line}</div>");
                    }
                    else // scrivo la linea matematica
                    {
                        await writer.WriteLineAsync(line);
                    }
                    continue;
                }
                // se non sono in un blocco matematico
                if (line.StartsWith("$$"))
                {
                    // verifico se il blocco matematico si trova sulla stessa linea
                    if (line.EndsWith("$$") && line.Length > 2)
                    {
                        CloseTags(writer);
                        await writer.WriteAsync($"<div class=\"math-block\">{line}</div>");
                    }
                    else // se no significa che devo aprire un blocco matematico nuovo
                    {
                        InMathBlock = true;
                        await writer.WriteLineAsync($"<div class=\"math-block\">{line}");
                    }
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
                // # GITHUB BLOCKQUOTE (admotions) - solo se non sono gia in un blocco blockquote
                if (line.StartsWith("> [!") && InBlockquote == false)
                {
                    // recupero il tipo che si trova qui: "> [!tipo]"
                    string type = line.Substring(4, line.IndexOf(']') - 4).ToLower();
                    CloseTags(writer);
                    await writer.WriteLineAsync($"<blockquote class=\"alert-{type}\">");
                    InBlockquote = true;
                    continue;
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
            int spaces = 0;
            foreach (char c in line)
            {
                if (c == ' ' || c == '\t') spaces++;
                else break;
            }
            CurrentIndent = spaces;
        }

        private void CloseTags(StreamWriter writer)
        {
            if (InParagraph) { writer.WriteLine("</p>"); InParagraph = false; }
            if (InTable) { writer.WriteLine("</tbody></table>"); InTable = false; }
            if (InBlockquote) { writer.WriteLine("</blockquote>"); InBlockquote = false; }
            if (InMathBlock) { writer.WriteLine("</div>"); InMathBlock = false; }
            while (ListStack.Count > 0)
            {
                writer.WriteLine(ListStack.Pop().tag);
            }
            CurrentIndent = 0;
        }

        private string ParseInline(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            StringBuilder sb = new();
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
                // codice inline: ` (non processa niente dentro)
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
                // icone di google material
                if (c == ':' && i + 1 < text.Length && text[i + 1] == ':')
                {
                    // recupero il contenuto
                    int start = i + 2; // skippo ::
                    int end = text.IndexOf("::", start);
                    if (end > start)
                    {
                        sb.Append($"<span class=\"material-symbols-rounded\">{text[start..end]}</span>");
                        i = end + 1;
                        continue;
                    }
                }
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

        private string GetEmbeddedCss()
        {
            var assembly = typeof(MdConverterPlugin).Assembly;
            string resourceName = "swiss.plugins.mdconverter.assets.md.min.css";
            using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        }

        public override void Help()
        {
            PrintHelp<MdConverterSettings>(false);
            ConsolePlus.Write("[Cyan]#[/] Feature supportate:");
            ConsolePlus.Write("[Cyan]#[/] - Google Icons (inserendo il nome delle icone in [Cyan]::nome_icona::[/])");
            ConsolePlus.Write("[Cyan]#[/] - KateX (tramite blocchi inline [Cyan]$$[/] e blocchi interi [Cyan]$$...$$[/])");
            ConsolePlus.Write("[Cyan]#[/] - Syntax Highlight per qualsiasi linguaggio di programmazione (grazie a PrismjJs)");
            ConsolePlus.Write("[Cyan]#[/] - Grafici tramite Mermaid.JS (live editor qui: https://mermaid.ai/live/edit), opzioni:");
            ConsolePlus.Write("[Cyan]#[/]   --mermaid-theme : definisci il tema che preferisci per i grafici");
            ConsolePlus.Write("[Cyan]#[/] - Github notes (tramite costrutti \"> [!WARNING|NOTE...]\")");
            ConsolePlus.WriteHr();
        }
    }
}
