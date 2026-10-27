using System.Numerics;

namespace RecursiveAI.OneEcma;

public readonly partial struct JsValue
{
    /// <summary>The boolean. Only meaningful when <see cref="IsBoolean"/>; any other value gives false.</summary>
    public bool AsBoolean => IsTrue;

    /// <summary>The BigInt. Only meaningful when <see cref="IsBigInt"/>; any other value throws <see cref="InvalidCastException"/>.</summary>
    public BigInteger AsBigInt => (BigInteger)Ref!;

    /// <summary>The symbol. Only meaningful when <see cref="IsSymbol"/>; any other value throws <see cref="InvalidCastException"/>.</summary>
    public JsSymbol AsSymbol => (JsSymbol)Ref!;
}
