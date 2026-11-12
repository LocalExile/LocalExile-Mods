using System.Globalization;
using GameServer.Core.Protocol.Instance;

namespace LocalExile.Mods.AiOperator;

/// <summary>
/// The <c>/aiop</c> chat commands. Every subcommand is read-only: the only thing that leaves the server is the chat
/// reply itself — no walking, no teleporting, no state change.
/// </summary>
internal static class AiCommands
{
    /// <summary>What <c>/aiop</c> prints when it is asked for something it does not have.</summary>
    public const string Usage =
        "/aiop world [radius] (the world model: one AI {json} reply, long replies go on in AI+ lines; radius in " +
        "cells, default 60) | /aiop reach x y (whether the character can walk there, and what blocks it) | " +
        "/aiop item id (whether that ground item is within pickup range) | /aiop why id (one sentence on why a " +
        "monster or item cannot be used now)";

    public static async Task Run(ClientState conn, string[] args)
    {
        if (args.Length == 0)
        {
            await Reply(conn, Usage);
            return;
        }

        string what = args[0].ToLowerInvariant();
        if (what == "world")
        {
            double radius = WorldModel.DefaultRadius;
            if (args.Length > 1 && (!double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out radius)
                    || radius <= 0 || radius > WorldModel.MaxRadius))
            {
                await Reply(conn, $"{Usage} (the radius must be a number of cells between 1 and {WorldModel.MaxRadius:0})");
                return;
            }
            await Send(conn, WorldModel.Build(conn, radius));
            return;
        }

        if (what == "reach")
        {
            if (args.Length < 3 || !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                    || !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
            {
                await Reply(conn, $"{Usage} (reach takes two whole cells: /aiop reach 388 231)");
                return;
            }
            await Send(conn, Gate.Reach(conn, x, y));
            return;
        }

        if (what is "item" or "why")
        {
            if (args.Length < 2 || !uint.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id))
            {
                await Reply(conn, $"{Usage} ({what} takes an object id, as /aiop world or /state prints it)");
                return;
            }
            await Send(conn, what == "item" ? Gate.Item(conn, id) : Gate.Why(conn, id));
            return;
        }

        await Reply(conn, Usage);
    }

    static async Task Send(ClientState conn, System.Text.Json.Nodes.JsonObject answer)
    {
        foreach (string line in ChatLines.Split(answer.ToJsonString()))
            await Reply(conn, line);
    }

    static Task Reply(ClientState conn, string text) => conn.Commands?.Reply(text) ?? Task.CompletedTask;
}