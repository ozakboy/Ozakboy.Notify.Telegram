using System.Globalization;
using System.Text;
using Ozakboy.Notify.Telegram.Internal;

namespace Ozakboy.Notify.Telegram.Tests;

[TestClass]
public sealed class TelegramMessageSplitterTests
{
    private const int Limit = TelegramNotifierOptions.MaxMessageLength;

    [TestMethod]
    [DataRow(NotificationLevel.Info, "[INFO] hello")]
    [DataRow(NotificationLevel.Warning, "[WARNING] hello")]
    [DataRow(NotificationLevel.Critical, "[CRITICAL] hello")]
    public void Split_ShortText_IsOneMessageWithLevelFirst(NotificationLevel level, string expected)
    {
        var parts = TelegramMessageSplitter.Split(level, "hello", 10);

        Assert.AreEqual(1, parts.Count);
        Assert.AreEqual(expected, parts[0]);
    }

    [TestMethod]
    public void Split_BlankText_SendsPlaceholderRatherThanNothing()
    {
        var parts = TelegramMessageSplitter.Split(NotificationLevel.Warning, "  \n ", 10);

        Assert.AreEqual(1, parts.Count);
        Assert.AreEqual("[WARNING] " + TelegramMessageSplitter.EmptyTextPlaceholder, parts[0]);
    }

    [TestMethod]
    public void Split_ReservedMarkdownCharacters_AreLeftUntouched()
    {
        // 純文字送出:底線、星號、角括號都原樣保留,不做任何跳脫。
        // Plain text: underscores, asterisks, and angle brackets are left exactly as written.
        const string Text = "BTC_USDT *止損* <失敗> [x](y) `code` ~!#+-=|{}.";

        var parts = TelegramMessageSplitter.Split(NotificationLevel.Critical, Text, 10);

        Assert.AreEqual("[CRITICAL] " + Text, parts[0]);
    }

    [TestMethod]
    public void Split_ExactlyAtLimit_StaysSingle_OneMoreCharacterSplits()
    {
        var fits = new string('a', Limit - "[INFO] ".Length);
        var tooLong = fits + "a";

        Assert.AreEqual(1, TelegramMessageSplitter.Split(NotificationLevel.Info, fits, 10).Count);
        Assert.AreEqual(Limit, TelegramMessageSplitter.Split(NotificationLevel.Info, fits, 10)[0].Length);
        Assert.AreEqual(2, TelegramMessageSplitter.Split(NotificationLevel.Info, tooLong, 10).Count);
    }

    [TestMethod]
    public void Split_ManyLines_BreaksOnLineBoundariesAndKeepsOrder()
    {
        var lines = Enumerable.Range(1, 400)
            .Select(i => string.Create(CultureInfo.InvariantCulture, $"line {i:000}: {new string('x', 40)}"))
            .ToArray();
        var text = string.Join('\n', lines);

        var parts = TelegramMessageSplitter.Split(NotificationLevel.Critical, text, 10);

        Assert.IsTrue(parts.Count > 1);
        AssertWithinLimit(parts);
        for (var i = 0; i < parts.Count; i++)
        {
            StringAssert.StartsWith(parts[i], $"[CRITICAL] ({i + 1}/{parts.Count})\n");
        }

        // 依行切分:把每段內文用換行接回去,必須與原文完全相同(沒有任何一行被切斷)。
        // Split on lines: joining the bodies with line breaks must reproduce the original exactly — no line cut.
        Assert.AreEqual(text, string.Join('\n', Bodies(parts)));
    }

    [TestMethod]
    public void Split_OneVeryLongLine_IsCutAndReassemblesExactly()
    {
        var text = new string('a', 10_000);

        var parts = TelegramMessageSplitter.Split(NotificationLevel.Info, text, 10);

        Assert.AreEqual(3, parts.Count);
        AssertWithinLimit(parts);
        Assert.AreEqual(text, string.Concat(Bodies(parts)));
    }

    [TestMethod]
    public void Split_Emoji_NeverBreaksASurrogatePair()
    {
        var text = string.Concat(Enumerable.Repeat("\U0001F600", 3000));

        var parts = TelegramMessageSplitter.Split(NotificationLevel.Info, text, 10);

        Assert.IsTrue(parts.Count > 1);
        AssertWithinLimit(parts);
        foreach (var body in Bodies(parts))
        {
            AssertValidUtf16(body);
        }

        Assert.AreEqual(text, string.Concat(Bodies(parts)));
    }

    [TestMethod]
    public void Split_ZwjFamilyEmoji_NeverBreaksTheSequence()
    {
        // 一個家庭 emoji 由 7 個純量值、11 個 UTF-16 字元組成,切在中間會變成幾個不相干的人。
        // A family emoji is 7 scalars and 11 UTF-16 units; cutting inside it turns it into unrelated people.
        const string Family = "\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466";
        var text = string.Concat(Enumerable.Repeat(Family, 600));

        var parts = TelegramMessageSplitter.Split(NotificationLevel.Info, text, 10);

        Assert.IsTrue(parts.Count > 1);
        AssertWithinLimit(parts);
        foreach (var body in Bodies(parts))
        {
            Assert.AreEqual(0, body.Length % Family.Length, "每段必須是完整的家庭 emoji 串。Every part must hold whole sequences.");
        }

        Assert.AreEqual(text, string.Concat(Bodies(parts)));
    }

    [TestMethod]
    public void Split_CombiningMarks_StayWithTheirBaseLetter()
    {
        const string Accented = "é";
        var text = string.Concat(Enumerable.Repeat(Accented, 3000));

        var parts = TelegramMessageSplitter.Split(NotificationLevel.Info, text, 10);

        foreach (var body in Bodies(parts))
        {
            Assert.AreNotEqual('́', body[0], "組合符號不可與它的基底字母分開。A combining mark must not be split from its base letter.");
        }

        Assert.AreEqual(text, string.Concat(Bodies(parts)));
    }

    [TestMethod]
    public void Split_SingleGraphemeLongerThanLimit_StillTerminatesAndStaysValid()
    {
        var text = "a" + new string('́', 9000);

        var parts = TelegramMessageSplitter.Split(NotificationLevel.Info, text, 10);

        AssertWithinLimit(parts);
        Assert.AreEqual(text, string.Concat(Bodies(parts)));
    }

    [TestMethod]
    public void Split_BeyondMaxParts_TruncatesAndSaysHowMuch()
    {
        var text = new string('a', 20_000);

        var parts = TelegramMessageSplitter.Split(NotificationLevel.Critical, text, 2);

        Assert.AreEqual(2, parts.Count);
        AssertWithinLimit(parts);
        StringAssert.Contains(parts[1], "[TRUNCATED: ");
        StringAssert.EndsWith(parts[1], " more characters not sent]");
    }

    [TestMethod]
    public void FindCut_NeverEndsBetweenSurrogates()
    {
        var text = "ab\U0001F600cd";

        // 預算 3 會落在代理字組中間,必須退到 2。
        // A budget of 3 lands inside the surrogate pair and must back off to 2.
        Assert.AreEqual(2, TelegramMessageSplitter.FindCut(text, 3));
        Assert.AreEqual(4, TelegramMessageSplitter.FindCut(text, 4));
    }

    private static void AssertWithinLimit(List<string> parts)
    {
        foreach (var part in parts)
        {
            Assert.IsTrue(part.Length <= Limit, $"分段長度 {part.Length} 超過上限。A part of {part.Length} exceeds the limit.");
        }
    }

    private static void AssertValidUtf16(string text)
    {
        Assert.IsTrue(text.EnumerateRunes().All(rune => rune != Rune.ReplacementChar), "不可含孤立的代理字元。Must hold no lone surrogate.");
        Assert.IsFalse(char.IsHighSurrogate(text[^1]), "不可以高代理字元結尾。Must not end on a high surrogate.");
        Assert.IsFalse(char.IsLowSurrogate(text[0]), "不可以低代理字元開頭。Must not start on a low surrogate.");
    }

    private static IEnumerable<string> Bodies(List<string> parts) =>
        parts.Count == 1
            ? [parts[0][(parts[0].IndexOf(' ', StringComparison.Ordinal) + 1)..]]
            : parts.Select(part => part[(part.IndexOf('\n', StringComparison.Ordinal) + 1)..]);
}
