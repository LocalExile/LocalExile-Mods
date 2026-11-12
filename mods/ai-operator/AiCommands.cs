using System.Globalization;
using GameServer.Core.Protocol.Instance;

namespace LocalExile.Mods.AiOperator;

/// <summary>
/// The <c>/ai</c> chat command. Every subcommand is read-only: the only thing that leaves the server is the chat
/// reply itself — no walking, no teleporting, no state change.
/// </summary>
internal static class AiCommands
{
    /// <summary>What <c>/ai</c> prints when it is asked for something it does not have.</summary>
    public const string Usage =
        "/aiop world [radius] (read-only: one AI {json} reply for the world model; long replies go on in AI+ lines; radius in cells, default 60)";

    public static async Task Run(ClientState conn, string[] args)
    {
        if (args.Length == 0 || !args[0].Equals("world", StringComparison.OrdinalIgnoreCase))
        {
            await Reply(conn, Usage);
            return;
        }

        double radius = WorldModel.DefaultRadius;
        if (args.Length > 1 && (!double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out radius)
                || radius <= 0 || radius > WorldModel.MaxRadius))
        {
            await Reply(conn, $"{Usage} (the radius must be a number of cells between 1 and {WorldModel.MaxRadius:0})");
            return;
        }

        foreach (string line in ChatLines.Split(WorldModel.Build(conn, radius).ToJsonString()))
            await Reply(conn, line);
    }

    static Task Reply(ClientState conn, string text) => conn.Commands?.Reply(text) ?? Task.CompletedTask;
}
