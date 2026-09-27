/* ****************************************************************************
 *
 * crypt(3) - the traditional DES-based one - for platforms without a libcrypt.
 *
 * Windows has no crypt(3); MRI's Windows build compiles its own (missing/crypt.c,
 * the classic BSD implementation), so String#crypt answers there exactly as a
 * Unix libc's DES crypt does.  This is the same algorithm: the first eight
 * characters of the password, seven bits each, are the DES key; the two salt
 * characters perturb the expansion table, twelve bits of them, one swap per bit;
 * a block of zeros is encrypted 25 times; and the 64 bits that come out are
 * written as eleven characters of the ./0-9A-Za-z alphabet after the salt.
 * Written for clarity rather than speed, as the V7 code it follows was.
 *
 * ***************************************************************************/

using System;

namespace IronRuby.Builtins {

    internal static class UnixCrypt {

        private static readonly byte[] IP = {
            58,50,42,34,26,18,10, 2, 60,52,44,36,28,20,12, 4,
            62,54,46,38,30,22,14, 6, 64,56,48,40,32,24,16, 8,
            57,49,41,33,25,17, 9, 1, 59,51,43,35,27,19,11, 3,
            61,53,45,37,29,21,13, 5, 63,55,47,39,31,23,15, 7,
        };

        private static readonly byte[] FP = {
            40, 8,48,16,56,24,64,32, 39, 7,47,15,55,23,63,31,
            38, 6,46,14,54,22,62,30, 37, 5,45,13,53,21,61,29,
            36, 4,44,12,52,20,60,28, 35, 3,43,11,51,19,59,27,
            34, 2,42,10,50,18,58,26, 33, 1,41, 9,49,17,57,25,
        };

        private static readonly byte[] PC1_C = {
            57,49,41,33,25,17, 9,  1,58,50,42,34,26,18,
            10, 2,59,51,43,35,27, 19,11, 3,60,52,44,36,
        };

        private static readonly byte[] PC1_D = {
            63,55,47,39,31,23,15,  7,62,54,46,38,30,22,
            14, 6,61,53,45,37,29, 21,13, 5,28,20,12, 4,
        };

        private static readonly byte[] Shifts = { 1,1,2,2,2,2,2,2,1,2,2,2,2,2,2,1 };

        private static readonly byte[] PC2_C = {
            14,17,11,24, 1, 5,  3,28,15, 6,21,10,
            23,19,12, 4,26, 8, 16, 7,27,20,13, 2,
        };

        private static readonly byte[] PC2_D = {
            41,52,31,37,47,55, 30,40,51,45,33,48,
            44,49,39,56,34,53, 46,42,50,36,29,32,
        };

        private static readonly byte[] ExpansionTable = {
            32, 1, 2, 3, 4, 5,  4, 5, 6, 7, 8, 9,
             8, 9,10,11,12,13, 12,13,14,15,16,17,
            16,17,18,19,20,21, 20,21,22,23,24,25,
            24,25,26,27,28,29, 28,29,30,31,32, 1,
        };

        private static readonly byte[,] S = {
            { 14, 4,13, 1, 2,15,11, 8, 3,10, 6,12, 5, 9, 0, 7,
               0,15, 7, 4,14, 2,13, 1,10, 6,12,11, 9, 5, 3, 8,
               4, 1,14, 8,13, 6, 2,11,15,12, 9, 7, 3,10, 5, 0,
              15,12, 8, 2, 4, 9, 1, 7, 5,11, 3,14,10, 0, 6,13 },
            { 15, 1, 8,14, 6,11, 3, 4, 9, 7, 2,13,12, 0, 5,10,
               3,13, 4, 7,15, 2, 8,14,12, 0, 1,10, 6, 9,11, 5,
               0,14, 7,11,10, 4,13, 1, 5, 8,12, 6, 9, 3, 2,15,
              13, 8,10, 1, 3,15, 4, 2,11, 6, 7,12, 0, 5,14, 9 },
            { 10, 0, 9,14, 6, 3,15, 5, 1,13,12, 7,11, 4, 2, 8,
              13, 7, 0, 9, 3, 4, 6,10, 2, 8, 5,14,12,11,15, 1,
              13, 6, 4, 9, 8,15, 3, 0,11, 1, 2,12, 5,10,14, 7,
               1,10,13, 0, 6, 9, 8, 7, 4,15,14, 3,11, 5, 2,12 },
            {  7,13,14, 3, 0, 6, 9,10, 1, 2, 8, 5,11,12, 4,15,
              13, 8,11, 5, 6,15, 0, 3, 4, 7, 2,12, 1,10,14, 9,
              10, 6, 9, 0,12,11, 7,13,15, 1, 3,14, 5, 2, 8, 4,
               3,15, 0, 6,10, 1,13, 8, 9, 4, 5,11,12, 7, 2,14 },
            {  2,12, 4, 1, 7,10,11, 6, 8, 5, 3,15,13, 0,14, 9,
              14,11, 2,12, 4, 7,13, 1, 5, 0,15,10, 3, 9, 8, 6,
               4, 2, 1,11,10,13, 7, 8,15, 9,12, 5, 6, 3, 0,14,
              11, 8,12, 7, 1,14, 2,13, 6,15, 0, 9,10, 4, 5, 3 },
            { 12, 1,10,15, 9, 2, 6, 8, 0,13, 3, 4,14, 7, 5,11,
              10,15, 4, 2, 7,12, 9, 5, 6, 1,13,14, 0,11, 3, 8,
               9,14,15, 5, 2, 8,12, 3, 7, 0, 4,10, 1,13,11, 6,
               4, 3, 2,12, 9, 5,15,10,11,14, 1, 7, 6, 0, 8,13 },
            {  4,11, 2,14,15, 0, 8,13, 3,12, 9, 7, 5,10, 6, 1,
              13, 0,11, 7, 4, 9, 1,10,14, 3, 5,12, 2,15, 8, 6,
               1, 4,11,13,12, 3, 7,14,10,15, 6, 8, 0, 5, 9, 2,
               6,11,13, 8, 1, 4,10, 7, 9, 5, 0,15,14, 2, 3,12 },
            { 13, 2, 8, 4, 6,15,11, 1,10, 9, 3,14, 5, 0,12, 7,
               1,15,13, 8,10, 3, 7, 4,12, 5, 6,11, 0,14, 9, 2,
               7,11, 4, 1, 9,12,14, 2, 0, 6,10,13,15, 3, 5, 8,
               2, 1,14, 7, 4,10, 8,13,15,12, 9, 0, 3, 5, 6,11 },
        };

        private static readonly byte[] P = {
            16, 7,20,21, 29,12,28,17,  1,15,23,26,  5,18,31,10,
             2, 8,24,14, 32,27, 3, 9, 19,13,30, 6, 22,11, 4,25,
        };

        /// <summary>
        /// crypt(key, salt) with the traditional DES algorithm. <paramref name="key"/> is read up
        /// to its first NUL or its 8th byte; <paramref name="salt"/> must have two bytes.
        /// </summary>
        internal static string/*!*/ Crypt(byte[]/*!*/ key, byte[]/*!*/ salt) {
            var block = new byte[66];
            for (int i = 0, k = 0; k < key.Length && key[k] != 0 && i < 64; k++) {
                int c = key[k];
                for (int j = 0; j < 7; j++, i++) {
                    block[i] = (byte)((c >> (6 - j)) & 1);
                }
                i++;
            }

            var schedule = KeySchedule(block);
            var expansion = (byte[])ExpansionTable.Clone();

            var output = new char[13];
            for (int i = 0; i < 2; i++) {
                int c = salt[i];
                output[i] = (char)c;
                if (c > 'Z') c -= 6;
                if (c > '9') c -= 7;
                c -= '.';
                for (int j = 0; j < 6; j++) {
                    if (((c >> j) & 1) != 0) {
                        byte t = expansion[6 * i + j];
                        expansion[6 * i + j] = expansion[6 * i + j + 24];
                        expansion[6 * i + j + 24] = t;
                    }
                }
            }

            Array.Clear(block, 0, block.Length);
            for (int i = 0; i < 25; i++) {
                Encrypt(block, schedule, expansion);
            }

            for (int i = 0; i < 11; i++) {
                int c = 0;
                for (int j = 0; j < 6; j++) {
                    c <<= 1;
                    c |= (6 * i + j < 66) ? block[6 * i + j] : 0;
                }
                c += '.';
                if (c > '9') c += 7;
                if (c > 'Z') c += 6;
                output[i + 2] = (char)c;
            }
            if (output[1] == 0) {
                output[1] = output[0];
            }
            return new string(output);
        }

        private static byte[][]/*!*/ KeySchedule(byte[]/*!*/ key) {
            var c = new byte[28];
            var d = new byte[28];
            for (int i = 0; i < 28; i++) {
                c[i] = key[PC1_C[i] - 1];
                d[i] = key[PC1_D[i] - 1];
            }
            var schedule = new byte[16][];
            for (int i = 0; i < 16; i++) {
                for (int k = 0; k < Shifts[i]; k++) {
                    byte t = c[0];
                    Array.Copy(c, 1, c, 0, 27);
                    c[27] = t;
                    t = d[0];
                    Array.Copy(d, 1, d, 0, 27);
                    d[27] = t;
                }
                var ks = new byte[48];
                for (int j = 0; j < 24; j++) {
                    ks[j] = c[PC2_C[j] - 1];
                    ks[j + 24] = d[PC2_D[j] - 28 - 1];
                }
                schedule[i] = ks;
            }
            return schedule;
        }

        private static void Encrypt(byte[]/*!*/ block, byte[][]/*!*/ schedule, byte[]/*!*/ expansion) {
            var lr = new byte[64];      // L is lr[0..31], R is lr[32..63]
            for (int j = 0; j < 64; j++) {
                lr[j] = block[IP[j] - 1];
            }

            var tempL = new byte[32];
            var preS = new byte[48];
            var f = new byte[32];
            for (int i = 0; i < 16; i++) {
                Array.Copy(lr, 32, tempL, 0, 32);
                byte[] ks = schedule[i];
                for (int j = 0; j < 48; j++) {
                    preS[j] = (byte)(lr[32 + expansion[j] - 1] ^ ks[j]);
                }
                for (int j = 0; j < 8; j++) {
                    int t = 6 * j;
                    int k = S[j, (preS[t] << 5) + (preS[t + 1] << 3) + (preS[t + 2] << 2) +
                                 (preS[t + 3] << 1) + preS[t + 4] + (preS[t + 5] << 4)];
                    t = 4 * j;
                    f[t] = (byte)((k >> 3) & 1);
                    f[t + 1] = (byte)((k >> 2) & 1);
                    f[t + 2] = (byte)((k >> 1) & 1);
                    f[t + 3] = (byte)(k & 1);
                }
                for (int j = 0; j < 32; j++) {
                    lr[32 + j] = (byte)(lr[j] ^ f[P[j] - 1]);
                }
                Array.Copy(tempL, 0, lr, 0, 32);
            }
            for (int j = 0; j < 32; j++) {
                byte t = lr[j];
                lr[j] = lr[32 + j];
                lr[32 + j] = t;
            }
            for (int j = 0; j < 64; j++) {
                block[j] = lr[FP[j] - 1];
            }
        }
    }
}
