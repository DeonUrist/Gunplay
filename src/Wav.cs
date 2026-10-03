using System;
using System.IO;

namespace Gunplay
{
    // WAV reader: PCM 8/16/24/32-bit, IEEE float 32/64, WAVE_FORMAT_EXTENSIBLE. Returns interleaved samples in -1..1.
    // No UnityEngine types, so it can be tested outside the game.
    internal static class Wav
    {
        public static float[] Read(byte[] b, out int channels, out int rate)
        {
            channels = 0; rate = 0;
            if (b.Length < 12 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
                throw new InvalidDataException("not a RIFF/WAVE file");
            int fmt = 0, bits = 0, dataOff = -1, dataLen = 0;
            int p = 12;
            while (p + 8 <= b.Length)
            {
                string id = new string(new[] { (char)b[p], (char)b[p + 1], (char)b[p + 2], (char)b[p + 3] });
                int len = BitConverter.ToInt32(b, p + 4);
                int body = p + 8;
                if (len < 0 || body + len > b.Length) len = b.Length - body;   // tolerate a wrong size in the last chunk
                if (id == "fmt ")
                {
                    fmt = BitConverter.ToUInt16(b, body);
                    channels = BitConverter.ToUInt16(b, body + 2);
                    rate = BitConverter.ToInt32(b, body + 4);
                    bits = BitConverter.ToUInt16(b, body + 14);
                    if (fmt == 0xFFFE && len >= 26) fmt = BitConverter.ToUInt16(b, body + 24);   // extensible: sub-format GUID starts with the real tag
                }
                else if (id == "data") { dataOff = body; dataLen = len; }
                p = body + len + (len & 1);
            }
            if (channels <= 0 || rate <= 0 || dataOff < 0) throw new InvalidDataException("missing fmt or data chunk");
            int bps = bits / 8;
            if (bps <= 0) throw new InvalidDataException("bad bit depth " + bits);
            int n = dataLen / bps;
            n -= n % channels;
            var s = new float[n];
            for (int i = 0; i < n; i++)
            {
                int o = dataOff + i * bps;
                if (fmt == 3)
                    s[i] = bps == 8 ? (float)BitConverter.ToDouble(b, o) : BitConverter.ToSingle(b, o);
                else if (fmt == 1)
                {
                    switch (bps)
                    {
                        case 1: s[i] = (b[o] - 128) / 128f; break;
                        case 2: s[i] = BitConverter.ToInt16(b, o) / 32768f; break;
                        case 3: s[i] = ((b[o] | (b[o + 1] << 8) | (b[o + 2] << 16)) << 8 >> 8) / 8388608f; break;
                        case 4: s[i] = BitConverter.ToInt32(b, o) / 2147483648f; break;
                        default: throw new InvalidDataException("unsupported PCM bit depth " + bits);
                    }
                }
                else throw new InvalidDataException("unsupported WAV format " + fmt + " (save as PCM or float WAV)");
            }
            return s;
        }
    }
}
