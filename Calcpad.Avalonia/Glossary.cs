using System.Collections.Generic;

namespace Calcpad.Avalonia
{
    // Short descriptions shown when hovering a token in the editor (units, functions, constants).
    internal static class CalcpadGlossary
    {
        public static string? Describe(string word)
        {
            if (Units.TryGetValue(word, out var u)) return $"{word} — {u} (unit)";
            if (Functions.TryGetValue(word, out var f)) return $"{word}() — {f} (function)";
            if (Constants.TryGetValue(word, out var c)) return $"{word} — {c} (constant)";
            return null;
        }

        private static readonly Dictionary<string, string> Units = new()
        {
            // length
            ["m"] = "metre, length", ["cm"] = "centimetre", ["mm"] = "millimetre", ["km"] = "kilometre",
            ["in"] = "inch", ["ft"] = "foot", ["yd"] = "yard", ["mi"] = "mile",
            // mass
            ["g"] = "gram, mass", ["kg"] = "kilogram", ["mg"] = "milligram", ["t"] = "tonne",
            ["lb"] = "pound (mass)", ["oz"] = "ounce",
            // time
            ["s"] = "second, time", ["ms"] = "millisecond", ["min"] = "minute", ["h"] = "hour", ["d"] = "day",
            // force
            ["N"] = "newton, force", ["kN"] = "kilonewton", ["MN"] = "meganewton",
            ["lbf"] = "pound-force", ["kip"] = "kip (1000 lbf)",
            // pressure / stress
            ["Pa"] = "pascal, pressure", ["kPa"] = "kilopascal", ["MPa"] = "megapascal", ["GPa"] = "gigapascal",
            ["bar"] = "bar", ["psi"] = "pound per sq. inch", ["ksi"] = "kip per sq. inch",
            // energy / power
            ["J"] = "joule, energy", ["kJ"] = "kilojoule", ["MJ"] = "megajoule",
            ["W"] = "watt, power", ["kW"] = "kilowatt", ["MW"] = "megawatt",
            ["Wh"] = "watt-hour", ["kWh"] = "kilowatt-hour",
            // electrical
            ["V"] = "volt, potential", ["kV"] = "kilovolt", ["mV"] = "millivolt",
            ["A"] = "ampere, current", ["mA"] = "milliampere", ["kA"] = "kiloampere",
            ["Ω"] = "ohm, resistance", ["ohm"] = "ohm, resistance", ["kΩ"] = "kilohm", ["MΩ"] = "megohm",
            ["C"] = "coulomb, charge", ["F"] = "farad, capacitance", ["H"] = "henry, inductance",
            ["Hz"] = "hertz, frequency", ["kHz"] = "kilohertz", ["MHz"] = "megahertz", ["GHz"] = "gigahertz",
            ["Wb"] = "weber, magnetic flux", ["T"] = "tesla, magnetic field", ["S"] = "siemens",
            ["VA"] = "volt-ampere", ["VAR"] = "volt-ampere reactive", ["Ah"] = "ampere-hour",
            // other
            ["K"] = "kelvin, temperature", ["mol"] = "mole, amount", ["cd"] = "candela",
            ["lm"] = "lumen", ["lx"] = "lux", ["L"] = "litre, volume", ["mL"] = "millilitre",
            ["rad"] = "radian, angle", ["deg"] = "degree, angle", ["grad"] = "gradian",
            ["Bq"] = "becquerel", ["Gy"] = "gray", ["Sv"] = "sievert",
        };

        private static readonly Dictionary<string, string> Functions = new()
        {
            ["sin"] = "sine", ["cos"] = "cosine", ["tan"] = "tangent",
            ["asin"] = "inverse sine", ["acos"] = "inverse cosine", ["atan"] = "inverse tangent",
            ["sinh"] = "hyperbolic sine", ["cosh"] = "hyperbolic cosine", ["tanh"] = "hyperbolic tangent",
            ["sqrt"] = "square root", ["sqr"] = "square root", ["cbrt"] = "cube root", ["root"] = "n-th root",
            ["abs"] = "absolute value / magnitude", ["sign"] = "sign (−1, 0, +1)", ["mod"] = "remainder",
            ["ln"] = "natural logarithm", ["log"] = "base-10 logarithm", ["log2"] = "base-2 logarithm",
            ["exp"] = "e raised to x", ["min"] = "minimum", ["max"] = "maximum",
            ["sum"] = "sum", ["product"] = "product", ["average"] = "arithmetic mean", ["mean"] = "mean",
            ["round"] = "round to nearest", ["floor"] = "round down", ["ceiling"] = "round up", ["trunc"] = "truncate",
            ["gcd"] = "greatest common divisor", ["lcm"] = "least common multiple", ["fact"] = "factorial",
            ["re"] = "real part", ["im"] = "imaginary part", ["phase"] = "complex phase angle",
            ["random"] = "random number", ["timer"] = "elapsed time",
        };

        private static readonly Dictionary<string, string> Constants = new()
        {
            ["π"] = "pi ≈ 3.141593", ["pi"] = "pi ≈ 3.141593", ["e"] = "Euler's number ≈ 2.718282",
            ["φ"] = "golden ratio ≈ 1.618034", ["γ"] = "Euler–Mascheroni ≈ 0.577216",
        };
    }
}
