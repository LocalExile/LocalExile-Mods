using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GameServer.Modding;

public sealed class PobMod : IMod, IDisposable
{
    readonly ConditionalWeakTable<IPlayer, Request> requests = new();
    readonly SemaphoreSlim converter = new(1, 1);
    IModContext context = null!;

    public void Load(IModContext mod)
    {
        context = mod;
        PobBridge.Settings = mod.Settings<PobSettings>();
        GameEvents.Command(mod.Manifest.Id, "pob", "/pob <code> | file <name> | confirm | cancel", Command);
        GameEvents.Packet(0x15).Add(mod.Manifest.Id, async (conn, bytes) =>
        {
            var player = Program.PlayerOf(conn);
            if (!requests.TryGetValue(player, out var request) || !request.HasPlan
                || !TryAnswer(bytes.Span, out bool yes)) return false;
            if (yes) await Confirm(player, request);
            else { request.Cancel(); await player.Tell("Импорт PoB отменён."); }
            return true;
        });
    }

    async Task Command(ClientState conn, string[] args)
    {
        var player = Program.PlayerOf(conn);
        var request = requests.GetValue(player, _ => new Request());
        if (args.Length == 1 && args[0].Equals("confirm", StringComparison.OrdinalIgnoreCase))
        { await Confirm(player, request); return; }
        if (args.Length == 1 && args[0].Equals("cancel", StringComparison.OrdinalIgnoreCase))
        { request.Cancel(); await player.Tell("Импорт PoB отменён."); return; }
        await Prepare(player, request, args.ToArray());
    }

    async Task Prepare(IPlayer player, Request request, string[] args)
    {
        long generation = request.Cancel();
        string? sourceFile = null, preparedJson = null;
        try
        {
            if (args.Length == 2 && args[0].Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                string name = args[1];
                if (name != Path.GetFileName(name) || name.Length > 80 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || !(name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Нужен файл .txt или .json из data/pob-import/inbox.");
                string path = Path.Combine("data", "pob-import", "inbox", name);
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > 2 * 1024 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Файл отсутствует, слишком большой или является ссылкой.");
                string content = File.ReadAllText(path).Trim();
                if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    using var snapshot = JsonDocument.Parse(content);
                    if (snapshot.RootElement.TryGetProperty("Version", out _)) preparedJson = content;
                    else { args = [snapshot.RootElement.GetProperty("pob").GetString() ?? ""]; sourceFile = path; }
                }
                else args = [content];
            }
            if (preparedJson is null && (args.Length != 1 || args[0].Length is < 8 or > 60000))
                throw new InvalidDataException("Использование: /pob <код> | file <имя> | confirm | cancel.");
            if (!await converter.WaitAsync(0, context.Lifetime.Stopping))
                throw new InvalidOperationException("Конвертер занят. Повторите команду немного позже.");
            try
            {
                string character = context.CharacterBuilds.Capture(player);
                await player.Tell("Код принят. Проверяю билд; персонаж пока не изменён...");
                string text = preparedJson ?? await PobBridge.Convert(args[0], character, sourceFile, context.Lifetime.Stopping);
                if (player.Closed || !context.Lifetime.IsActive || !request.Matches(generation)) return;
                var plan = context.CharacterBuilds.Prepare(player, text);
                if (!request.Stage(generation, plan)) { plan.Dispose(); return; }
                await player.Tell($"Код PoB валидный. {plan.Summary}\nБудут заменены класс, уровень, дерево, экипировка, камни и джевелы этого персонажа.");
                foreach (string warning in plan.Warnings.Take(8)) await player.Tell("PoB: " + Short(warning));
                if (plan.Warnings.Count > 8) await player.Tell($"PoB: всего предупреждений {plan.Warnings.Count}; показаны первые 8.");
                await player.Tell("Применить билд? Введите Y или /pob confirm в течение 5 минут. N или /pob cancel — отмена.");
            }
            finally { converter.Release(); }
        }
        catch (Exception e) when (e is InvalidDataException or FormatException or JsonException or IOException
            or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception
            or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException or NullReferenceException)
        {
            if (!player.Closed && context.Lifetime.IsActive)
                await player.Tell("PoB не применён: " + (e is OperationCanceledException ? "время проверки истекло." : Short(e.Message)));
        }
    }

    async Task Confirm(IPlayer player, Request request)
    {
        var (plan, generation) = request.Take();
        if (plan is null) { await player.Tell("Подготовьте билд заново через /pob."); return; }
        using (plan)
        {
            try
            {
                await player.Tell("Применение PoB подтверждено. Релог персонажа через...");
                for (int seconds = 3; seconds > 0; seconds--)
                {
                    if (player.Closed || !request.Matches(generation)) return;
                    await player.Tell(seconds + "...");
                    await Task.Delay(TimeSpan.FromSeconds(1), context.Lifetime.Stopping);
                }
                if (request.Matches(generation) && !plan.Confirm() && !player.Closed && context.Lifetime.IsActive)
                    await player.Tell("PoB не применён: подтверждение устарело или персонаж недоступен. Подготовьте билд заново.");
            }
            catch (OperationCanceledException) when (context.Lifetime.Stopping.IsCancellationRequested) { }
        }
    }

    // Y/N and optional ASCII channel prefixes only; other chat is left to the server's protocol reader.
    internal static bool TryAnswer(ReadOnlySpan<byte> packet, out bool yes)
    {
        yes = false;
        if (packet.Length < 5 || BinaryPrimitives.ReadUInt16BigEndian(packet) != 0x15) return false;
        int length = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        if (length is < 1 or > 128 || packet.Length != 5 + length || packet[^1] != 0) return false;
        var text = packet.Slice(4, length);
        foreach (byte b in text) if (b >= 128) return false;
        string body = Encoding.ASCII.GetString(text);
        if (body[0] == '@') return false;
        if (body[0] is '#' or '$' or '%' or '&') body = body[1..];
        body = body.Trim();
        yes = body.Equals("Y", StringComparison.OrdinalIgnoreCase);
        return yes || body.Equals("N", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() { foreach (var entry in requests) entry.Value.Cancel(); requests.Clear(); }
    static string Short(string text) => text.Replace('\r', ' ').Replace('\n', ' ') is var line && line.Length > 800 ? line[..800] : line;

    sealed class Request
    {
        readonly Lock gate = new();
        long generation;
        ICharacterBuildPlan? plan;
        public bool HasPlan { get { lock (gate) return plan is not null; } }
        public long Cancel() { lock (gate) { plan?.Dispose(); plan = null; return ++generation; } }
        public bool Matches(long value) { lock (gate) return value == generation; }
        public bool Stage(long value, ICharacterBuildPlan prepared)
        { lock (gate) { if (value != generation) return false; plan = prepared; return true; } }
        public (ICharacterBuildPlan? Plan, long Generation) Take()
        { lock (gate) { var prepared = plan; plan = null; return (prepared, generation); } }
    }
}
