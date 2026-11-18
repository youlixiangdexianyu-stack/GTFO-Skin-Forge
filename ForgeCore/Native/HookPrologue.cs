using System;
using Iced.Intel;
namespace ForgeCore;

/// <summary>Whether the native entry of an Il2Cpp method can carry the detour Il2CppInterop's Harmony support writes, judged from
/// its first <see cref="SnapshotLength"/> bytes before any patch is on it.</summary>
internal static class HookPrologue
{
    /// <summary>The bytes the pinned Dobby backend overwrites at the entry: its near slot jump (FF 25 rel32).</summary>
    internal const int OverwriteLength = 6;
    internal const int SnapshotLength = 32;

    // Dobby relocates every instruction that starts inside the bytes it overwrites. The pinned backend writes its
    // near slot jump (FF 25 rel32, OverwriteLength bytes; the in-game M12 admission
    // observed this form). Inside that range short bodies,
    // branches, calls, SIMD and unlisted instructions are refused; instructions starting after it are not moved.
    // The pinned Dobby decoder terminates preparation on legacy SSE (including F2/F3/66 and XMM forms);
    // copying them is not a supported backend operation.
    // MOV r64,imm64 crossing that range also refuses: Prepare reports success but its resume jump overwrites the
    // high immediate bytes in the trampoline. Shorter immediate moves and RIP-relative loads have separate proofs.
    // The bytes are read from the entry before the first patch on it (Hooks.Unsupported).
    // The one branch admitted is the tail jump of a thunk body (an IL2CPP wrapper such as `xor edx,edx; jmp real`): a
    // near rel32 jump that starts inside the range and is the last instruction of the function, proven by the 0xCC
    // padding that follows it. It may end past the range; the bytes beyond are then the rest of the same jump, which
    // the relocated copy carries and nothing else can reach.
    internal static string? Rejection(byte[] code)
    {
        const int overwrite = OverwriteLength;
        if (code.Length != SnapshotLength) return "invalid-or-truncated-native-prologue";
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(code));
        while (decoder.IP < overwrite)
        {
            decoder.Decode(out var instruction);
            if (instruction.IsInvalid) return "invalid-or-truncated-native-prologue";
            if (instruction.FlowControl == FlowControl.UnconditionalBranch && instruction.Length == 5
                && instruction.GetOpKind(0) == OpKind.NearBranch64)
            {
                var padding = true;
                for (var offset = (int)decoder.IP; offset < Math.Max((int)decoder.IP + 1, overwrite); offset++) padding &= code[offset] == 0xcc;
                if (padding) return null;
            }
            if (instruction.FlowControl is FlowControl.Return or FlowControl.UnconditionalBranch or FlowControl.IndirectBranch
                or FlowControl.Interrupt or FlowControl.Exception) return "short-or-branch-native-body";
            if (instruction.FlowControl == FlowControl.ConditionalBranch)
                return "unsupported-native-prologue:conditional-branch@" + (decoder.IP - (uint)instruction.Length);
            if (instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
                return "unsupported-native-prologue:call@" + (decoder.IP - (uint)instruction.Length);
            if (instruction.Code == Code.Mov_r64_imm64 && decoder.IP > overwrite)
                return "unsupported-native-prologue:mov-imm64-straddle@" + (decoder.IP - (uint)instruction.Length);
            for (var operand = 0; operand < instruction.OpCount; operand++)
                if (instruction.GetOpKind(operand) == OpKind.Register && !instruction.GetOpRegister(operand).IsGPR())
                    return "unsupported-native-prologue:" + instruction.Mnemonic + "@" + (decoder.IP - (uint)instruction.Length);
            if (instruction.Encoding != EncodingKind.Legacy || instruction.Mnemonic is not (
                Mnemonic.Add or Mnemonic.And or Mnemonic.Cmp or Mnemonic.Dec or Mnemonic.Inc
                or Mnemonic.Lea or Mnemonic.Mov or Mnemonic.Movsx or Mnemonic.Movsxd or Mnemonic.Movzx
                or Mnemonic.Nop or Mnemonic.Or or Mnemonic.Pop or Mnemonic.Push or Mnemonic.Sub or Mnemonic.Test
                or Mnemonic.Xchg or Mnemonic.Xor))
                return "unsupported-native-prologue:" + instruction.Mnemonic + "@" + (decoder.IP - (uint)instruction.Length);
        }
        return null;
    }
}
