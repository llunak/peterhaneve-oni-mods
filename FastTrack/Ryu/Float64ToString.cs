﻿/*
 * Copyright 2022 Peter Han
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this software
 * and associated documentation files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use, copy, modify, merge, publish,
 * distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all copies or
 * substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
 * BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
 * DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 * 
 * This is a substantial modification of https://github.com/ulfjack/ryu, which is available
 * under the Boost Software License, Version 1.0 (https://www.boost.org/LICENSE_1_0.txt).
 */

using System.Globalization;
using System.Text;

namespace Ryu {
	/// <summary>
	/// Converts doubles directly to exponential or fixed strings.
	/// </summary>
	internal static class Float64ToString {
		private const int DOUBLE_EXP_DIFF = RyuFloat64.DOUBLE_BIAS + RyuFloat64.
			DOUBLE_MANTISSA_BITS;

		private const int MANTISSA_SHIFT = 8;

		private const int POW10_ADDITIONAL_BITS = 120;

		/// <summary>
		/// Convert `digits` to decimal and append the last 9 decimal digits to result.
		/// If `digits` contains additional digits, then those are silently ignored.
		/// </summary>
		private static void Append9Digits(StringBuilder result, uint digits) {
			if (digits == 0U)
				RyuUtils.Append0(result, 9);
			else {
				int index = result.Length + 9;
				result.Length = index--;
				for (int i = 0; i < 2; i++) {
					uint d10000 = digits / 10000U, c0 = digits - 10000 * d10000;
					uint c1 = c0 / 100U;
					digits = d10000;
					index = result.WriteDigits(index, c0 - 100U * c1);
					index = result.WriteDigits(index, c1);
				}
				result[index] = digits.DigitToChar();
			}
		}

		/// <summary>
		/// Convert `digits` to decimal and write the last `count` decimal digits to result.
		/// If `digits` contains additional digits, then those are silently ignored.
		/// </summary>
		private static int AppendCDigits(StringBuilder result, uint digits, int count) {
			int i = 0, index = result.Length + count;
			result.Length = index--;
			for (; i < count - 1; i += 2) {
				uint d100 = digits / 100U, c = digits - 100U * d100;
				digits = d100;
				index = result.WriteDigits(index, c);
			}
			// Generate the last digit if count is odd
			if (i < count)
				result[index--] = (digits % 10U).DigitToChar();
			return index;
		}

		/// <summary>
		/// Convert `digits` to a sequence of decimal digits. Print the first digit, followed
		/// by a decimal separator followed by the remaining digits. The caller has to
		/// guarantee that:
		///   10^(olength-1) <= digits < 10^olength
		/// e.g., by passing `olength` as `decimalLength9(digits)`
		/// </summary>
		private static void AppendDDigits(StringBuilder result, uint digits, int count,
				bool printDecimalPoint, NumberFormatInfo info) {
			if (printDecimalPoint) {
				char dp = info.NumberDecimalSeparator[0];
				int index = result.Length + count;
				result.Length = index--;
				index = RyuUtils.PrintGroups42(result, ref digits, index);
				if (digits >= 10U) {
					string tbl = RyuTables.DIGIT_TABLE[digits];
					result[index--] = tbl[1];
					result[index--] = dp;
					result[index--] = tbl[0];
				} else {
					result[index--] = dp;
					result[index--] = digits.DigitToChar();
				}
			} else
				result.Append(digits.DigitToChar());
		}

		/// <summary>
		/// Convert `digits` to a sequence of decimal digits. Appends the digits to the result.
		/// The caller has to guarantee that:
		///   10^(olength-1) <= digits < 10^olength
		/// e.g., by passing `olength` as `decimalLength9(digits)`
		/// </summary>
		private static int AppendNDigits(StringBuilder result, int count, uint digits) {
			int index = result.Length + count;
			result.Length = index--;
			index = RyuUtils.PrintGroups42(result, ref digits, index);
			// Group of 1
			if (digits >= 10U)
				index = result.WriteDigits(index, digits);
			else
				result[index--] = digits.DigitToChar();
			return index;
		}

		/// <summary>
		/// Normalizes the IEEE float exponent and adds the implicit leading one for normal
		/// floats.
		/// </summary>
		private static int Decode(ulong ieeeMantissa, uint ieeeExponent, out ulong mantissa) {
			int exponent;
			if (ieeeExponent == 0U) {
				exponent = 1 - DOUBLE_EXP_DIFF;
				mantissa = ieeeMantissa;
			} else {
				exponent = (int)ieeeExponent - DOUBLE_EXP_DIFF;
				mantissa = (1UL << RyuFloat64.DOUBLE_MANTISSA_BITS) | ieeeMantissa;
			}
			return exponent;
		}

		private static bool HasTrailingZeroes(int exponent, int rexp, ulong mantissa) {
			int requiredTwos = -exponent - rexp;
			return requiredTwos <= 0 || (requiredTwos < 60 && RyuUtils.IsMultipleOf2Power(
				mantissa, requiredTwos));
		}

		private static int IndexForExponent(int e) => (e + 15) >> 4;

		private static uint LastDigit(ref uint digits, int iterations) {
			uint lastDigit = 0U, iter = digits;
			for (int i = iterations; i > 0; i--) {
				uint iter10 = iter / 10U;
				lastDigit = iter - 10U * iter10;
				iter = iter10;
			}
			digits = iter;
			return lastDigit;
		}

		private static int LengthForIndex(int idx) => (RyuUtils.Log10Pow2(idx << 4) + 25) / 9;

		private static int Pow10BitsForIndex(int idx) => (idx << 4) + POW10_ADDITIONAL_BITS;

		/// <summary>
		/// Convert a 64-bit floating point number to an exponential notation string.
		/// </summary>
		internal static void ToExponentialString(StringBuilder result, ulong ieeeMantissa,
				uint ieeeExponent, int precision, RyuFormatOptions options,
				NumberFormatInfo info) {
			bool printDP = precision > 0, soft = (options & RyuFormatOptions.SoftPrecision) !=
				0;
			uint digits = 0U;
			int printedDigits = 0, availDigits = 0, exp = 0, exponent = Decode(ieeeMantissa,
				ieeeExponent, out ulong mantissa);
			ulong mantShift = mantissa << MANTISSA_SHIFT;
			++precision;

			if (exponent >= -RyuFloat64.DOUBLE_MANTISSA_BITS) {
				int idx = (exponent < 0) ? 0 : IndexForExponent(exponent), i = LengthForIndex(
					idx) - 1, j = Pow10BitsForIndex(idx) - exponent + MANTISSA_SHIFT,
					p = RyuTables.POW10_OFFSET_D[idx] + i;
				for (; i >= 0; i--) {
					// Temporary: j is usually around 128, and by shifting a bit, we push it
					// to 128 or above, which is a slightly faster code path in
					// MulShiftMod1E9. Instead, we can just increase the multipliers
					digits = RyuUtils.MulShiftMod1E9(mantShift, RyuTables.POW10_SPLIT_D[p, 0],
						RyuTables.POW10_SPLIT_D[p, 1], RyuTables.POW10_SPLIT_D[p, 2], j);
					if (printedDigits > 0) {
						if (printedDigits + 9 > precision) {
							availDigits = 9;
							break;
						}
						Append9Digits(result, digits);
						printedDigits += 9;
					} else if (digits != 0U) {
						availDigits = RyuUtils.DecimalLength9(digits);
						exp = i * 9 + availDigits - 1;
						if (availDigits > precision)
							break;
						AppendDDigits(result, digits, availDigits + 1, printDP, info);
						printedDigits = availDigits;
						availDigits = 0;
					}
					p--;
				}
			}

			if (exponent < 0 && availDigits == 0) {
				int idx = (-exponent) >> 4, pMax = RyuTables.POW10_OFFSET_2_D[idx + 1], p =
					RyuTables.POW10_OFFSET_2_D[idx], j = MANTISSA_SHIFT +
					POW10_ADDITIONAL_BITS - exponent - (idx << 4);
				for (int i = RyuTables.MIN_BLOCK_2_D[idx]; i < 200; i++) {
					digits = (p >= pMax) ? 0U : RyuUtils.MulShiftMod1E9(mantShift,
						RyuTables.POW10_SPLIT_2_D[p, 0], RyuTables.POW10_SPLIT_2_D[p, 1],
						RyuTables.POW10_SPLIT_2_D[p, 2], j);
					if (printedDigits > 0) {
						if (printedDigits + 9 > precision) {
							availDigits = 9;
							break;
						}
						Append9Digits(result, digits);
						printedDigits += 9;
					} else if (digits != 0) {
						availDigits = RyuUtils.DecimalLength9(digits);
						exp = (i + 1) * -9 + availDigits - 1;
						if (availDigits > precision)
							break;
						AppendDDigits(result, digits, availDigits + 1, printDP, info);
						printedDigits = availDigits;
						availDigits = 0;
					}
					p++;
				}
			}

			// 0 = don't round up; 1 = round up unconditionally; 2 = round up if odd
			int maxDigits = precision - printedDigits, roundFlag;
			uint lastDigit = 0U;
			if (availDigits == 0)
				digits = 0U;
			if (availDigits > maxDigits)
				lastDigit = LastDigit(ref digits, availDigits - maxDigits);
			if (lastDigit != 5U)
				roundFlag = (lastDigit > 5U) ? 1 : 0;
			else {
				// Is m * 2^e2 * 10^(precision + 1 - exp) integer?
				// precision was already increased by 1, so we don't need to write + 1 here.
				int rexp = precision - exp;
				bool trailingZeroes = HasTrailingZeroes(exponent, rexp, mantissa);
				if (rexp < 0 && trailingZeroes)
					trailingZeroes = RyuUtils.IsMultipleOf5Power(mantissa, -rexp);
				roundFlag = trailingZeroes ? 2 : 1;
			}
			if (printedDigits > 0) {
				if (digits == 0U) {
					if (!soft)
						RyuUtils.Append0(result, maxDigits);
				} else
					AppendCDigits(result, digits, maxDigits);
			} else
				AppendDDigits(result, digits, maxDigits + 1, printDP, info);

			if (roundFlag != 0 && RyuUtils.RoundResult(result, roundFlag, out _, info))
				exp++;

			if (soft)
				RyuUtils.SoftenResult(result, info);

			RyuUtils.AppendExponent(result, exp, options, info);
		}

		/// <summary>
		/// Convert a 64-bit floating point number to a fixed notation string.
		/// </summary>
		internal static void ToFixedString(StringBuilder result, ulong ieeeMantissa,
				uint ieeeExponent, int precision, RyuFormatOptions options,
				NumberFormatInfo info) {
			bool zero = true, soft = (options & RyuFormatOptions.SoftPrecision) != 0;
			int exponent = Decode(ieeeMantissa, ieeeExponent, out ulong mantissa);
			ulong mShift = mantissa << MANTISSA_SHIFT;
			uint digits;

			if (exponent >= -RyuFloat64.DOUBLE_MANTISSA_BITS) {
				int idx = (exponent < 0) ? 0 : IndexForExponent(exponent), p10bits =
					Pow10BitsForIndex(idx), i = LengthForIndex(idx) - 1, p = RyuTables.
					POW10_OFFSET_D[idx] + i, j = p10bits - exponent + MANTISSA_SHIFT;
				for (; i >= 0; i--) {
					digits = RyuUtils.MulShiftMod1E9(mShift, RyuTables.POW10_SPLIT_D[p, 0],
						RyuTables.POW10_SPLIT_D[p, 1], RyuTables.POW10_SPLIT_D[p, 2], j);
					if (!zero)
						Append9Digits(result, digits);
					else if (digits != 0U) {
						AppendNDigits(result, RyuUtils.DecimalLength9(digits), digits);
						zero = false;
					}
					p--;
				}
			}

			if (zero)
				result.Append(RyuUtils.ZERO);
			if ((options & RyuFormatOptions.ThousandsSeparators) != 0)
				RyuUtils.AddThousands(result, info);
			if (precision > 0 && (!soft || exponent < 0))
				result.Append(info.NumberDecimalSeparator);

			if (exponent < 0) {
				// 0 = don't round up; 1 = round up unconditionally; 2 = round up if odd
				int idx = (-exponent) >> 4, roundFlag = 0, i = 0, blocks = precision / 9 + 1,
					minBlock = RyuTables.MIN_BLOCK_2_D[idx];
				if (blocks <= minBlock) {
					RyuUtils.Append0(result, precision);
					i = blocks;
				} else if (i < minBlock) {
					RyuUtils.Append0(result, 9 * minBlock);
					i = minBlock;
				}
				int p = RyuTables.POW10_OFFSET_2_D[idx] + i - minBlock, pMax = RyuTables.
					POW10_OFFSET_2_D[idx + 1], j = MANTISSA_SHIFT + POW10_ADDITIONAL_BITS -
					exponent - (idx << 4);
				for (; i < blocks; ++i) {
					if (p >= pMax) {
						// If the remaining digits are all 0, then no rounding required
						if (!soft)
							RyuUtils.Append0(result, precision - 9 * i);
						break;
					}
					digits = RyuUtils.MulShiftMod1E9(mShift, RyuTables.POW10_SPLIT_2_D[p, 0],
						RyuTables.POW10_SPLIT_2_D[p, 1], RyuTables.POW10_SPLIT_2_D[p, 2], j);
					if (i < blocks - 1)
						Append9Digits(result, digits);
					else {
						int maximum = precision - 9 * i;
						uint lastDigit = LastDigit(ref digits, 9 - maximum);
						// Is m * 10^(additionalDigits + 1) / 2^(-e2) integer?
						if (lastDigit > 5U)
							roundFlag = 1;
						else if (lastDigit < 5U)
							roundFlag = 0;
						else if (HasTrailingZeroes(exponent, precision + 1, mantissa))
							roundFlag = 2;
						else
							roundFlag = 1;
						if (maximum > 0)
							AppendCDigits(result, digits, maximum);
						break;
					}
					p++;
				}
				if (roundFlag != 0 && RyuUtils.RoundResult(result, roundFlag,
						out int decimalIndex, info)) {
					if (decimalIndex > 0) {
						result[decimalIndex++] = RyuUtils.ZERO;
						result[decimalIndex] = info.NumberDecimalSeparator[0];
					}
					if (!soft)
						result.Append(RyuUtils.ZERO);
				}
				if (soft && precision > 0)
					RyuUtils.SoftenResult(result, info);
			} else if (!soft)
				RyuUtils.Append0(result, precision);
		}
	}
}
