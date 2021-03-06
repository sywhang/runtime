// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using Xunit;

namespace System.Globalization.Tests
{
    public class DateTimeFormatInfoGetAbbreviatedDayName
    {
        public static IEnumerable<object[]> GetAbbreviatedDayName_TestData()
        {
            string[] englishAbbreviatedDayNames = new string[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            yield return new object[] { DateTimeFormatInfo.InvariantInfo, englishAbbreviatedDayNames };
            yield return new object[] { new CultureInfo("en-US").DateTimeFormat, englishAbbreviatedDayNames };
            yield return new object[] { new DateTimeFormatInfo(), englishAbbreviatedDayNames };

            if (!PlatformDetection.IsUbuntu)
            {
                yield return new object[] { new CultureInfo("fr-FR").DateTimeFormat, DateTimeFormatInfoData.FrFRAbbreviatedDayNames() };
            }
        }

        [Theory]
        [MemberData(nameof(GetAbbreviatedDayName_TestData))]
        public void GetAbbreviatedDayName_Invoke_ReturnsExpected(DateTimeFormatInfo info, string[] expected)
        {
            DayOfWeek[] values = new DayOfWeek[]
            {
                DayOfWeek.Sunday,
                DayOfWeek.Monday,
                DayOfWeek.Tuesday,
                DayOfWeek.Wednesday,
                DayOfWeek.Thursday,
                DayOfWeek.Friday,
                DayOfWeek.Saturday
            };

            for (int i = 0; i < values.Length; ++i)
            {
                Assert.Equal(expected[i], info.GetAbbreviatedDayName(values[i]));
            }
        }

        [Theory]
        [InlineData(DayOfWeek.Sunday - 1)]
        [InlineData(DayOfWeek.Saturday + 1)]
        public void GetAbbreviatedDayName_Invalid_ThrowsArgumentOutOfRangeException(DayOfWeek dayofweek)
        {
            var format = new DateTimeFormatInfo();
            AssertExtensions.Throws<ArgumentOutOfRangeException>("dayofweek", () => format.GetAbbreviatedDayName(dayofweek));
        }
    }
}
