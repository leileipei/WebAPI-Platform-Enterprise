using System;
using System.Globalization;

namespace Json.Pointer;

internal static class UtilityExtensions
{
	public static int AsInt(this ReadOnlySpan<char> value)
	{
		try
		{
#if NET8_0_OR_GREATER
			return int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
#else
			return int.Parse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture);
#endif
		}
		catch (FormatException e)
		{
			throw new PointerParseException("An invalid integer format was found", e);
		}
	}

	public static bool TryAsInt(this ReadOnlySpan<char> value, out int i)
	{
#if NET8_0_OR_GREATER
		return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out i);
#else
		return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i);
#endif
	}

	public static uint AsUint(this ReadOnlySpan<char> value)
	{
		try
		{
#if NET8_0_OR_GREATER
			return uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
#else
			return uint.Parse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture);
#endif
		}
		catch (FormatException e)
		{
			throw new PointerParseException("An invalid integer format was found", e);
		}
	}

	public static bool TryAsUint(this ReadOnlySpan<char> value, out uint i)
	{
#if NET8_0_OR_GREATER
		return uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out i);
#else
		return uint.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i);
#endif
	}
}