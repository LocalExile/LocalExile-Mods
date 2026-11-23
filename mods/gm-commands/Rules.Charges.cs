using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Core.Diagnostics;

static partial class GmCommandsRules
{
    internal const string ChargesUsage = "/charges [endurance|frenzy|power <count>] | /charges add <type> [count] | /charges clear " +
        "(dev: the charges held, their maximum, minimum and time left; set, gain or clear them)";

    // /charges: shows the charges, or sets (no refresh roll), gains (as a skill would: refreshes, can reach the maximum
    // chance) or clears them. The ticker applies the change within a step.
    internal static async Task ChargesCommand(Player player, string[] args)
    {
        var session = ChargesOf(player);
        if (args.Length == 0)
        {
            double now = player.Clock.Elapsed.TotalSeconds;
            string text = string.Join("; ", Charges.Types.Select(t =>
            {
                var l = session.Limits(t);
                return $"{Charges.Name(t)}{(session.Converted(t) ? $" (as {Charges.SpecialName(t)})" : "")} {session.Effective(t)}/{l.Maximum} (minimum {l.Minimum}, " +
                    $"{l.DurationMs / 1000.0:0.###} s, {session.State.Remaining(t, now):0.0} s left)";
            }));
            await player.Commands!.Reply($"Charges: {text}.");
            return;
        }
        if (args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            session.Enqueue(new ChargeEvent.Set(null, 0));
            await player.Commands!.Reply("Charges cleared.");
            return;
        }
        bool add = args[0].Equals("add", StringComparison.OrdinalIgnoreCase);
        if (add) args = args[1..];
        if (args.Length == 0 || Charges.Parse(args[0]) is not { } type) throw new ArgumentException("endurance, frenzy or power");
        int count = args.Length > 1 ? int.Parse(args[1]) : 1;
        if (count < 0) throw new ArgumentException("a count of 0 or more");
        session.Enqueue(add ? new ChargeEvent.Grant(type, count, "/charges add") : new ChargeEvent.Set(type, count));
        await player.Commands!.Reply($"{(add ? "Gaining" : "Setting")} {count} {Charges.Name(type)} charge(s).");
    }
}
