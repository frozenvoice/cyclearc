using CycleArc.Services;

namespace CycleArc.Tests;

public class DisplayStampFormattingTests
{
    [Fact]
    public void Stamps_ShareOneForm_AndNameTheYearOnlyWhenItDiffers()
    {
        var previous = (UiText.Language, DisplayFormatting.Use12HourClock);
        try
        {
            DisplayFormatting.Use12HourClock = false;
            var value = new DateTimeOffset(2026, 10, 15, 21, 29, 0, TimeSpan.Zero);
            var local = value.ToLocalTime();
            var sameYear = new DateTimeOffset(local.Year, 12, 31, 0, 0, 0, local.Offset);
            var nextYear = sameYear.AddYears(1);

            UiText.SetLanguage(UiLanguage.Korean);
            Assert.Equal($"{local.Month}월 {local.Day}일 {local:HH:mm}", DisplayFormatting.FormatStamp(value, sameYear));
            Assert.Equal($"{local.Year}년 {local.Month}월 {local.Day}일 {local:HH:mm}", DisplayFormatting.FormatStamp(value, nextYear));

            UiText.SetLanguage(UiLanguage.English);
            var month = local.ToString("MMM", CultureInfo.InvariantCulture);
            Assert.Equal($"{month} {local.Day} {local:HH:mm}", DisplayFormatting.FormatStamp(value, sameYear));
            Assert.Equal($"{month} {local.Day}, {local.Year} {local:HH:mm}", DisplayFormatting.FormatStamp(value, nextYear));
        }
        finally
        {
            UiText.SetLanguage(previous.Language);
            DisplayFormatting.Use12HourClock = previous.Use12HourClock;
        }
    }

    [Fact]
    public void EnglishFollowsTheTwelveHourPreference_KoreanKeepsTwentyFourHours()
    {
        var previous = (UiText.Language, DisplayFormatting.Use12HourClock);
        try
        {
            var value = new DateTimeOffset(2026, 10, 15, 21, 5, 0, TimeSpan.Zero);
            var local = value.ToLocalTime();
            DisplayFormatting.Use12HourClock = true;
            UiText.SetLanguage(UiLanguage.English);
            Assert.Equal(local.ToString("h:mm tt", CultureInfo.InvariantCulture), DisplayFormatting.FormatTime(value));
            Assert.Matches("^[0-9]{1,2}:[0-9]{2} (AM|PM)$", DisplayFormatting.FormatTime(value));
            UiText.SetLanguage(UiLanguage.Korean);
            Assert.Equal(local.ToString("HH:mm", CultureInfo.InvariantCulture), DisplayFormatting.FormatTime(value));

            Assert.True(DisplayFormatting.CultureUses12HourClock(CultureInfo.GetCultureInfo("en-US")));
            Assert.False(DisplayFormatting.CultureUses12HourClock(CultureInfo.GetCultureInfo("en-GB")));
            Assert.False(DisplayFormatting.CultureUses12HourClock(CultureInfo.GetCultureInfo("de-DE")));
        }
        finally
        {
            UiText.SetLanguage(previous.Language);
            DisplayFormatting.Use12HourClock = previous.Use12HourClock;
        }
    }
}
