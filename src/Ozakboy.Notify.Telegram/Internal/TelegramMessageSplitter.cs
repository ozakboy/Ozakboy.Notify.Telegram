using System.Globalization;
using System.Text;

namespace Ozakboy.Notify.Telegram.Internal;

/// <summary>
/// 把一則通知排成一或多則 Telegram 訊息:加上等級標記,過長時依行切分。
/// Lays a notification out as one or more Telegram messages: adds the level marker and, when too long, splits
/// along line breaks.
/// </summary>
/// <remarks>
/// <para>
/// 切分的優先順序是:換行 → 字素(grapheme cluster)邊界 → Unicode 純量值邊界。
/// 依行切是為了可讀性(告警多半是一行一個欄位);必須在行內切時,切點落在字素邊界上,
/// 一個 emoji(代理字組)、一組 ZWJ 家庭 emoji、一個帶組合符號的字都不會被拆成兩半 ——
/// 拆開的代理字組是無效的 UTF-16,Telegram 會直接拒收整則訊息,而那往往正是告警最需要送達的時候。
/// The order of preference is: line break, then grapheme-cluster boundary, then Unicode scalar boundary.
/// Splitting on lines keeps things readable (an alert is usually one field per line). When a line has to be cut,
/// the cut lands on a grapheme boundary, so an emoji (a surrogate pair), a ZWJ family sequence, or a letter with
/// combining marks is never broken in two — a split surrogate pair is invalid UTF-16, Telegram rejects the whole
/// message, and that tends to happen exactly when the alert most needs to get through.
/// </para>
/// <para>
/// 單一字素本身就超過上限(例如疊了上千個組合符號)時,才退到純量值邊界切 —— 至少保證不會切出孤立的代理字元,
/// 也保證迴圈一定前進。
/// Only when a single grapheme exceeds the limit on its own (thousands of stacked combining marks, say) does it
/// fall back to scalar boundaries: that still never produces a lone surrogate, and it guarantees progress.
/// </para>
/// </remarks>
internal static class TelegramMessageSplitter
{
    /// <summary>
    /// 內容為空白時送出的替代文字。Telegram 拒收空白訊息,而一則「有事發生但沒寫內容」的告警仍然值得送達。
    /// The stand-in sent for blank content. Telegram rejects blank messages, and an alert that says "something
    /// happened" without detail is still worth delivering.
    /// </summary>
    internal const string EmptyTextPlaceholder = "(empty)";

    /// <summary>
    /// 截斷時為註記保留的字元數,足以容納最長的註記文字。
    /// The room reserved for the truncation note, enough for its longest form.
    /// </summary>
    private const int TruncationNoticeReserve = 64;

    public static List<string> Split(NotificationLevel level, string text, int maxParts)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParts, 1);

        var label = GetLabel(level);
        var body = string.IsNullOrWhiteSpace(text) ? EmptyTextPlaceholder : text;

        // 放得下就不加分段標記:大部分告警都是一則,標記只是雜訊。
        // No part marker when it fits: most alerts are a single message, where the marker is only noise.
        if (label.Length + 1 + body.Length <= TelegramNotifierOptions.MaxMessageLength)
        {
            return [label + " " + body];
        }

        // 以最寬的分段標記(例如 " (10/10)")預留空間,確保每段加上標記後都不超過上限。
        // Reserve room for the widest marker (such as " (10/10)") so every part stays within the limit once
        // its marker is added.
        var widestMarker = FormatMarker(maxParts, maxParts);
        var bodyBudget = TelegramNotifierOptions.MaxMessageLength - label.Length - widestMarker.Length - 1;

        var chunks = SplitBody(body, bodyBudget);
        if (chunks.Count > maxParts)
        {
            chunks = Truncate(chunks, maxParts, bodyBudget);
        }

        if (chunks.Count == 1)
        {
            return [label + " " + chunks[0]];
        }

        var parts = new List<string>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            parts.Add(label + FormatMarker(i + 1, chunks.Count) + "\n" + chunks[i]);
        }

        return parts;
    }

    internal static string GetLabel(NotificationLevel level) => level switch
    {
        NotificationLevel.Info => "[INFO]",
        NotificationLevel.Warning => "[WARNING]",
        NotificationLevel.Critical => "[CRITICAL]",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "通知等級未定義。The notification level is not defined."),
    };

    /// <summary>
    /// 回傳不超過 <paramref name="budget"/> 的最長前綴長度,切點不落在字素中間。
    /// Returns the length of the longest prefix within <paramref name="budget"/> whose end does not fall inside a
    /// grapheme.
    /// </summary>
    internal static int FindCut(ReadOnlySpan<char> text, int budget)
    {
        var length = 0;
        while (length < text.Length)
        {
            var step = StringInfo.GetNextTextElementLength(text[length..]);
            if (length + step > budget)
            {
                // 第一個字素就放不下:只能在它內部切,退到純量值邊界。
                // Not even the first grapheme fits: it has to be cut inside, at scalar boundaries.
                return length > 0 ? length : CutAtScalarBoundary(text, budget);
            }

            length += step;
        }

        return length;
    }

    private static int CutAtScalarBoundary(ReadOnlySpan<char> text, int budget)
    {
        var length = 0;
        while (length < text.Length)
        {
            System.Text.Rune.DecodeFromUtf16(text[length..], out _, out var consumed);
            if (length + consumed > budget)
            {
                break;
            }

            length += consumed;
        }

        return length;
    }

    private static List<string> SplitBody(string body, int budget)
    {
        var chunks = new List<string>();
        var current = new StringBuilder(budget);
        var lineStart = 0;

        while (lineStart < body.Length)
        {
            var newline = body.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? body.Length : newline + 1;
            var line = body.AsSpan(lineStart, lineEnd - lineStart);
            lineStart = lineEnd;

            if (current.Length + line.Length <= budget)
            {
                current.Append(line);
                continue;
            }

            Flush(current, chunks);
            if (line.Length <= budget)
            {
                current.Append(line);
                continue;
            }

            // 單行就超過上限:只能在行內切。
            // A single line is over the limit on its own: it has to be cut within the line.
            var offset = 0;
            while (line.Length - offset > budget)
            {
                var cut = FindCut(line[offset..], budget);
                current.Append(line.Slice(offset, cut));
                Flush(current, chunks);
                offset += cut;
            }

            current.Append(line[offset..]);
        }

        Flush(current, chunks);
        return chunks;
    }

    private static void Flush(StringBuilder current, List<string> chunks)
    {
        if (current.Length == 0)
        {
            return;
        }

        // 段落尾端的換行在切分處沒有意義;只剩空白的段落 Telegram 會拒收,直接略過。
        // Trailing line breaks mean nothing at a split point, and Telegram rejects an all-blank part, so such a
        // part is skipped.
        var chunk = current.ToString().TrimEnd('\r', '\n');
        current.Clear();

        if (!string.IsNullOrWhiteSpace(chunk))
        {
            chunks.Add(chunk);
        }
    }

    private static List<string> Truncate(List<string> chunks, int maxParts, int budget)
    {
        var kept = chunks.GetRange(0, maxParts);

        long dropped = 0;
        for (var i = maxParts; i < chunks.Count; i++)
        {
            dropped += chunks[i].Length;
        }

        var last = kept[^1];
        var room = budget - TruncationNoticeReserve;
        if (last.Length > room)
        {
            var cut = FindCut(last, room);
            dropped += last.Length - cut;
            last = last[..cut];
        }

        kept[^1] = last + string.Create(CultureInfo.InvariantCulture, $"\n[TRUNCATED: {dropped} more characters not sent]");
        return kept;
    }

    private static string FormatMarker(int index, int total) =>
        string.Create(CultureInfo.InvariantCulture, $" ({index}/{total})");
}
