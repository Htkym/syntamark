using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Syntamark;

namespace Syntamark.Prose.Sample;

internal sealed record ProseDiagnostic(string Rule, string Severity, string Message,
    IReadOnlyList<RawSpan> RawFragments, MarkdownMappingPrecision Precision);

// Explicit sample integration: Syntamark itself has no Python or proofreading dependency.
internal static class YomiyasuBridge
{
    internal static async Task<IReadOnlyList<ProseDiagnostic>> RunAsync(ProseSelection selection,
        string pythonExecutable, string lintScript, CancellationToken cancellationToken)
    {
        if (!selection.HasSyntax || selection.Facts.Status == MarkdownParseStatus.Failed)
            throw new InvalidOperationException("Proofreading requires an available syntax snapshot.");
        var diagnostics = new List<ProseDiagnostic>();
        foreach (var segment in selection.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var coordinates = new YomiCoordinates(segment.Text);
            var start = new ProcessStartInfo(pythonExecutable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            start.ArgumentList.Add(lintScript);
            start.ArgumentList.Add("--json");
            start.ArgumentList.Add("--no-words");
            start.ArgumentList.Add("-");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Yomiyasu.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.StandardInput.WriteAsync(coordinates.Text.AsMemory(), timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                var json = await output;
                var stderr = await error;
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"Yomiyasu exited with {process.ExitCode}: {stderr}");
                using var result = JsonDocument.Parse(json);
                if (result.RootElement.GetProperty("format").GetString() != "yomiyasu-lint"
                    || result.RootElement.GetProperty("format_version").GetInt32() != 2)
                    throw new InvalidOperationException("Unsupported Yomiyasu lint output format.");
                foreach (var finding in result.RootElement.GetProperty("findings").EnumerateArray())
                    diagnostics.Add(Map(segment, finding));
            }
            finally
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
        }
        return diagnostics.AsReadOnly();
    }

    internal static ProseDiagnostic Map(ProseSegment segment, JsonElement finding)
    {
        var localLine = new YomiCoordinates(segment.Text).ToRawLine(finding.GetProperty("line").GetInt32());
        var mapped = segment.Map(localLine);
        // Lint format 2 reports a line, not an exact token range suitable for replacement.
        var precision = mapped.Precision == MarkdownMappingPrecision.Exact
            ? MarkdownMappingPrecision.CoveringTokens : mapped.Precision;
        return new(finding.GetProperty("rule").GetString()!, finding.GetProperty("severity").GetString()!,
            finding.GetProperty("message").GetString()!, mapped.RawFragments, precision);
    }
}
