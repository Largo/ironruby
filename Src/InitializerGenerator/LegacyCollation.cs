using System;
using System.Collections.Generic;

namespace IronRuby.InitializerGenerator {
    /// <summary>
    /// The order the initializers have always been written in.
    ///
    /// ClassInitGenerator sorted modules, methods and constants with the culture-sensitive String.CompareTo,
    /// which on .NET is ICU's root collation: punctuation, then digits, then letters with "a" before "A" and
    /// case only breaking ties ("Integer" before "IO", "__send__" before "!"). The order is visible to Ruby
    /// (Module#instance_methods and #constants enumerate in definition order), so it is kept; but a generator
    /// that runs in every build must not depend on the culture or the globalization mode of the machine it
    /// runs on, so the comparison is spelled out here for the characters names are made of.
    /// </summary>
    internal sealed class LegacyCollation : IComparer<string> {
        public static readonly LegacyCollation Instance = new LegacyCollation();

        // ICU's order of the printable ASCII characters that are not letters.
        private const string NonLetters = " _-,;:!?.'\"()[]{}@*/\\&#%`^+<=>|~$0123456789";

        private static readonly int[] _primary = MakePrimaryWeights();

        private static int[] MakePrimaryWeights() {
            var result = new int[128];
            for (int i = 0; i < result.Length; i++) {
                // control characters: not in any name, ordered by code ahead of everything else
                result[i] = i;
            }
            for (int i = 0; i < NonLetters.Length; i++) {
                result[NonLetters[i]] = 0x100 + i;
            }
            for (int i = 0; i < 26; i++) {
                result['a' + i] = result['A' + i] = 0x200 + i;
            }
            return result;
        }

        private static int Primary(char c) {
            // a non-ASCII character sorts after the ASCII ones, by code
            return c < 128 ? _primary[c] : 0x1000 + c;
        }

        public int Compare(string x, string y) {
            if ((object)x == (object)y) {
                return 0;
            }
            if (x == null) {
                return -1;
            }
            if (y == null) {
                return 1;
            }

            // letters and punctuation first, over the whole string ...
            int common = Math.Min(x.Length, y.Length);
            for (int i = 0; i < common; i++) {
                int px = Primary(x[i]), py = Primary(y[i]);
                if (px != py) {
                    return px < py ? -1 : 1;
                }
            }
            if (x.Length != y.Length) {
                return x.Length < y.Length ? -1 : 1;
            }

            // ... and only then case: lower before upper at the first difference
            for (int i = 0; i < common; i++) {
                if (x[i] != y[i]) {
                    return x[i] > y[i] ? -1 : 1;
                }
            }
            return 0;
        }
    }
}
