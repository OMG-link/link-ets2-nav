using LinkEts2Nav.SpeechProtocol;

namespace LinkEts2Nav.Speech;

// Parts are ordered and can repeat in a recording. Each distinct input is synthesized at most once.
internal sealed record BudgetPhrase(GenerationCandidate Usage, string[] Parts)
{
    internal string Text => Usage.Text;

    internal static BudgetPhrase From(GenerationCandidate candidate)
    {
        string text = candidate.Text;
        var parts = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (!Boundary(i)) continue;
            int end = i + 1;
            while (end < text.Length && (Boundary(end) || "”’）)]」』".Contains(text[end]))) end++;
            string piece = text[start..end].Trim();
            if (piece.Any(char.IsLetterOrDigit)) { parts.Add(piece); start = end; }
            i = end - 1;
        }
        if (start < text.Length && text[start..].Trim() is { Length: > 0 } tail)
        {
            if (!tail.Any(char.IsLetterOrDigit)) return new(candidate, []);
            parts.Add(tail);
        }
        return new(candidate, parts.Count > 1 ? parts.ToArray() : []);

        bool Boundary(int index)
        {
            char c = text[index];
            if ("；。！？;!?".Contains(c)) return true;
            // A period can belong to a road name ("St. Louis"), a decimal or an abbreviation.
            return c is ',' or '，' && !(index > 0 && index + 1 < text.Length && char.IsDigit(text[index - 1]) && char.IsDigit(text[index + 1]));
        }
    }
}

// Empty inputs retain an already verified complete file from the selected pack.
internal sealed record BudgetRecording(string Text, bool Composed, string[] Inputs);

internal static class SpeechBudgetPlanner
{
    private sealed record Offer(BudgetPhrase Phrase, bool Composed, string[] DistinctInputs, double EffectiveCost);

    internal static BudgetRecording[] Select(BudgetPhrase[] phrases, IReadOnlyDictionary<string, bool> retained,
        IReadOnlySet<string> replaceVoice, IReadOnlyDictionary<string, long> costs, long budget, CancellationToken token)
    {
        var ordered = phrases.OrderByDescending(p => p.Usage.PlayedCount).ThenByDescending(p => p.Usage.PreparationMilliseconds)
            .ThenBy(p => p.Text, StringComparer.Ordinal).ToArray();
        var owners = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in ordered.Where(p => !retained.ContainsKey(p.Text)))
            foreach (string part in row.Parts.Distinct(StringComparer.Ordinal))
                owners[part] = owners.GetValueOrDefault(part) + row.Usage.PlayedCount;

        var whole = new List<Offer>();
        var joint = new List<Offer>();
        foreach (var row in ordered)
        {
            if (retained.ContainsKey(row.Text)) continue;
            var exact = new Offer(row, false, [row.Text], costs[row.Text]);
            whole.Add(exact);
            joint.Add(exact);
            if (row.Parts.Length == 0) continue;
            string[] inputs = row.Parts.Distinct(StringComparer.Ordinal).ToArray();
            // A common clause earns playback coverage on each phrase that needs it. Actual reservations
            // below always charge the full counter price the first time a clip is purchased.
            double share = inputs.Sum(p => costs[p] * ((double)row.Usage.PlayedCount / owners[p]));
            joint.Add(new(row, true, inputs, share));
        }

        static IOrderedEnumerable<Offer> ByValue(IEnumerable<Offer> offers, bool weightWhole = true) =>
            offers.OrderByDescending(o => o.EffectiveCost == 0 ? double.PositiveInfinity :
                    (weightWhole && !o.Composed ? 2.0 : 1.0) * o.Phrase.Usage.PlayedCount / o.EffectiveCost)
                .ThenByDescending(o => o.Phrase.Usage.PlayedCount).ThenByDescending(o => o.Phrase.Usage.PreparationMilliseconds)
                .ThenBy(o => o.Phrase.Text, StringComparer.Ordinal).ThenBy(o => o.Composed);

        // Shared-cost coverage is a budgeted set-selection problem. Compare deterministic feasible
        // plans; density alone can skip a popular long recording in favor of one inexpensive clip.
        var plans = new[] { Evaluate(ByValue(joint)), Evaluate(ByValue(joint, false)), Evaluate(ByValue(whole)), Evaluate(whole) };
        // At each actual playback, a whole recording is worth two, a composed recording one,
        // and live synthesis zero. Equal quality favors covering more real announcements.
        return plans.OrderByDescending(p => p.Coverage + p.WholeCoverage).ThenByDescending(p => p.Coverage)
            .ThenBy(p => p.Cost).First().Recordings;

        (BudgetRecording[] Recordings, decimal Coverage, decimal WholeCoverage, long Cost) Evaluate(IEnumerable<Offer> offers)
        {
            var acquired = new HashSet<string>(StringComparer.Ordinal);
            var chosen = new Dictionary<string, BudgetRecording>(StringComparer.Ordinal);
            long remaining = budget;
            foreach (var row in ordered)
            {
                if (retained.TryGetValue(row.Text, out bool composed)) chosen.Add(row.Text, new(row.Text, composed, []));
            }
            foreach (var offer in offers)
            {
                token.ThrowIfCancellationRequested();
                if (chosen.ContainsKey(offer.Phrase.Text)) continue;
                long charge = 0;
                foreach (string input in offer.DistinctInputs)
                {
                    if (acquired.Contains(input)) continue;
                    if (costs[input] > remaining - charge) { charge = -1; break; }
                    charge += costs[input];
                }
                if (charge < 0) continue;
                remaining -= charge;
                acquired.UnionWith(offer.DistinctInputs);
                var row = offer.Phrase;
                chosen.Add(row.Text, new(row.Text, offer.Composed, offer.Composed ? row.Parts : [row.Text]));
            }

            // Previously synthesized whole texts may themselves be clauses in another phrase.
            // Spend remaining budget on upgrading the most-played composed recordings, returning
            // the cost of any newly unneeded clauses to the same budget.
            var uses = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in chosen.Values)
                foreach (string part in r.Inputs.Distinct(StringComparer.Ordinal)) uses[part] = uses.GetValueOrDefault(part) + 1;
            // Upgrade composed speech before spending anything solely on changing the voice
            // of an already complete recording. Each group keeps the measured popularity order.
            foreach (var row in ordered.Where(p => chosen.GetValueOrDefault(p.Text)?.Composed == true)
                .Concat(ordered.Where(p => replaceVoice.Contains(p.Text) && retained.GetValueOrDefault(p.Text) == false)))
            {
                if (!chosen.TryGetValue(row.Text, out var r) || (!r.Composed && !replaceVoice.Contains(row.Text))) continue;
                var inputs = r.Inputs.Distinct(StringComparer.Ordinal).ToArray();
                long refund = inputs.Where(p => uses[p] == 1).Sum(p => costs[p]);
                long charge = uses.ContainsKey(row.Text) ? 0 : costs[row.Text];
                if (charge > remaining + refund) continue;
                remaining += refund;
                remaining -= charge;
                foreach (string key in inputs) if (--uses[key] == 0) uses.Remove(key);
                uses[row.Text] = uses.GetValueOrDefault(row.Text) + 1;
                chosen[row.Text] = new(row.Text, false, [row.Text]);
            }
            var result = ordered.Where(p => chosen.ContainsKey(p.Text)).ToArray();
            return (result.Select(p => chosen[p.Text]).ToArray(), result.Sum(p => (decimal)p.Usage.PlayedCount),
                result.Where(p => !chosen[p.Text].Composed).Sum(p => (decimal)p.Usage.PlayedCount), budget - remaining);
        }
    }
}
