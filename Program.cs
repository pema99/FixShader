using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.RegularExpressions;

public class Program
{
    public static void Main()
    {
        new Program().Start();
    }

    [DllImport("msys-lz4-1.dll")]
    static extern int LZ4_decompress_safe(
        byte[] src, 
        byte[] dst, 
        int compressedSize, 
        int dstCapacity);

    [Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ID3DBlob
    {
        [PreserveSig] IntPtr GetBufferPointer();
        [PreserveSig] int GetBufferSize();
    }

    [PreserveSig]
    [DllImport("D3DCompiler_47.dll")]
    extern static int D3DDisassemble(
        byte[] pSrcData,
        UIntPtr SrcDataSize,
        uint flags,
        [MarshalAs(UnmanagedType.LPStr)] string? szComments,
        ref ID3DBlob? ppDisassembly);

    static string ID3DBlobToString(ID3DBlob? blob)
    {
        if (blob == null) return "";
        return Marshal.PtrToStringAnsi(blob.GetBufferPointer(), (int) blob.GetBufferSize());
    }

    string DisassembleDXBC(byte[] binary)
    {
        uint flags = 0; 

        ID3DBlob? res = null;
        D3DDisassemble(binary, new UIntPtr((uint)binary.Length), flags, null, ref res);
        return ID3DBlobToString(res);
    }

    byte[] HexStringToBytes(string hex)
    {
        byte[] res = new byte[hex.Length / 2];
        for (int i = 0; i < res.Length; i++)
        {
            string hexLiteral = hex.Substring(i * 2, 2);
            byte val = byte.Parse(hexLiteral, System.Globalization.NumberStyles.HexNumber);
            res[i] = val;
        }
        return res;
    }

    // This should work for the most part, but not using it currently since it's easier for the time being
    // to just scan for DXBC magic bytes instead.
    byte[] GetBlobData(byte[] decomp, uint index)
    {
        bool isOldSerialization = false; // version=1

        uint tabOffset = BitConverter.ToUInt32(decomp, (isOldSerialization ? 3 : 4) * sizeof(uint)) * index;
        uint offset = BitConverter.ToUInt32(decomp, (int)tabOffset + 1 * sizeof(uint));
        uint length = BitConverter.ToUInt32(decomp, (int)tabOffset + 2 * sizeof(uint));

        byte[] res = new byte[length];
        Array.Copy(decomp, offset, res, 0, length);
        return res;
    }

    uint GetBlobUintAtByteOffset(byte[] blob, uint offset) => BitConverter.ToUInt32(blob, (int)offset);
    uint GetBlobUint(byte[] blob, uint offset) => BitConverter.ToUInt32(blob, sizeof(uint) * (int)offset);
    uint GetBlobVersion(byte[] blob) => GetBlobUint(blob, 0);
    uint GetBlobType(byte[] blob) => GetBlobUint(blob, 1);
    uint GetBlobKeywordAmount(byte[] blob) => GetBlobUint(blob, 6);

    uint GetBlobKeywordSkipOffset(byte[] blob)
    {
        uint version = GetBlobVersion(blob);
        uint kw = GetBlobKeywordAmount(blob);
        uint currOffset = 7;
        for (uint i = 0; i < kw; i++)
        {
            uint strlen = GetBlobUint(blob, currOffset++);
            uint realOffset = (sizeof(uint) * currOffset) + strlen;
            realOffset = (realOffset + ((uint)4 - (uint)1)) & ~((uint)4 - (uint)1); // cursed alignment shit
            currOffset = realOffset / sizeof(uint);
        }

        // local kw
        if (version >= 201806140 && version < 202012090)
        {
            uint kwlocal = GetBlobUint(blob, currOffset++);
            for (uint i = 0; i < kwlocal; i++)
            {
                uint strlen = GetBlobUint(blob, currOffset++);
                uint realOffset = (sizeof(uint) * currOffset) + strlen;
                realOffset = (realOffset + ((uint)4 - (uint)1)) & ~((uint)4 - (uint)1); // cursed alignment shit
                currOffset = realOffset / sizeof(uint);
            }
        }

        return currOffset + 1; // skip code size
    }

    // Bleh this is pretty lame
    int FindNextDXBCBlob(byte[] blob, int start)
    {
        for (int i = start; i < blob.Length; i++)
        {
            if (blob[i] == 'D' && blob[i+1] == 'X' && blob[i+2] == 'B' && blob[i+3] == 'C')
            {
                return i;
            }
        }
        return -1; 
    }

    public void Start()
    {
        string yaml = File.ReadAllText("examples/Pema99AcidSpiral.asset");
        string compressedBlob = Regex.Match(yaml, "compressedBlob: ([a-fA-F0-9]*)\n").Groups[1].Value;
        byte[] bytes = HexStringToBytes(compressedBlob);

        // hardcoded decompressed length, lmao. Real solution is to parse the yaml file and read decompressedLengths
        byte[] blob = new byte[12344];
        LZ4_decompress_safe(bytes, blob, bytes.Length, 12344);

        int codeOffset = 0;
        int blobIdx = 0;
        while ((codeOffset = FindNextDXBCBlob(blob, codeOffset)) != -1)
        {
            uint codeSize = GetBlobUintAtByteOffset(blob, (uint)codeOffset + 24);
            byte[] gpuCode = new byte[codeSize];
            Array.Copy(blob, codeOffset, gpuCode, 0, gpuCode.Length);

            if (!Directory.Exists("out"))
                Directory.CreateDirectory("out");

            File.WriteAllBytes($"out/out{blobIdx}.dxbc", gpuCode);

            string disasm = DisassembleDXBC(gpuCode);
            File.WriteAllText($"out/out{blobIdx}.asm", disasm);

            Process.Start("thirdparty/cmd_Decompiler.exe", $"-D out/out{blobIdx}.dxbc");

            codeOffset += (int)codeSize;
            blobIdx++;
        }

        Console.WriteLine("Done");
    }
}
