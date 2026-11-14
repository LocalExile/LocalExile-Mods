using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;


sealed class PobSettings
{
    // Operator-owned executable and fixed arguments. Chat input is sent only through stdin.
    public string ConverterExecutable { get; set; } = "";
    public string[] ConverterArguments { get; set; } = [];
}

static class PobBridge
{
    internal const int Version = 1, MaxOutput = 8 * 1024 * 1024;
    internal static PobSettings Settings = new();

    internal static async Task<string> Convert(string code, string characterJson, string? sourceFile, CancellationToken stopping)
    {
        using var character = JsonDocument.Parse(characterJson);
        string executable = Settings.ConverterExecutable;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidDataException("Внешний конвертер не настроен (мод pob / настройки конвертера).");
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in Settings.ConverterArguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить внешний конвертер.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var output = ReadLimited(process.StandardOutput, MaxOutput, timeout.Token);
        var error = ReadLimited(process.StandardError, 8192, timeout.Token);
        // Cancel the process as soon as either pipe exceeds its limit, even if it stops writing or never exits.
        _ = output.ContinueWith(_ => timeout.Cancel(), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _ = error.ContinueWith(_ => timeout.Cancel(), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            // Framework JSON nodes avoid caching a collectible mod's anonymous request type globally.
            var input = new JsonObject
            {
                ["Version"] = Version, ["Code"] = code, ["Name"] = character.RootElement.GetProperty("Name").GetString(),
                ["Data"] = Path.GetFullPath("data"), ["Record"] = JsonNode.Parse(characterJson),
                ["SourceFile"] = sourceFile is null ? null : Path.GetFullPath(sourceFile),
            };
            await process.StandardInput.WriteAsync(input.ToJsonString().AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            string text = await output, errors = await error;
            if (process.ExitCode != 0) throw new InvalidDataException(errors.Trim() is { Length: > 0 } e ? e : "Внешний конвертер отклонил билд.");
            return text;
        }
        catch (OperationCanceledException) when (output.IsFaulted || error.IsFaulted)
        {
            if (output.IsFaulted) await output;
            await error;
            throw;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(output, error); } catch (Exception e) when (e is OperationCanceledException or InvalidDataException or IOException) { }
        }
    }

    internal static async Task<string> ReadLimited(TextReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder();
        char[] buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (read > limit - result.Length) throw new InvalidDataException("Ответ внешнего конвертера слишком большой.");
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }

}
