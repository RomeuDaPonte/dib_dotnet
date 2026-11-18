namespace Dib4t.SharedKernel;

/// <summary>
/// A value as a legacy page holds it in a JavaScript variable: a stored number, the text of an input once typed
/// (<c>"12"</c>, <c>""</c>), <c>null</c> (stored null) or <c>undefined</c> (missing), with the JavaScript conversions
/// the legacy formulas apply to it (<c>Number(x)</c>, truthiness, <c>String(x)</c>).
/// </summary>
public readonly record struct JsValue
{
    private readonly int _kind; // 0 undefined, 1 null, 2 number, 3 text
    private readonly double _number;
    private readonly string? _text;

    private JsValue(int kind, double number, string? text)
    {
        _kind = kind;
        _number = number;
        _text = text;
    }

    public static JsValue Undefined => default;

    public static JsValue Null => new(1, 0, null);

    public static JsValue Of(double value) => new(2, value, null);

    /// <summary>A number, or <c>null</c> for a stored <c>null</c>.</summary>
    public static JsValue Of(double? value) => value is { } number ? Of(number) : Null;

    /// <summary>The text of an input (<c>null</c> → <see cref="Null"/>).</summary>
    public static JsValue Text(string? value) => value is null ? Null : new(3, 0, value);

    public bool IsUndefined => _kind == 0;

    public bool IsNull => _kind == 1;

    public bool IsText => _kind == 3;

    /// <summary><c>Number(x)</c>: undefined → NaN, null → 0, a text by <see cref="JsNumber.FromText"/>.</summary>
    public double ToNumber() => _kind switch
    {
        0 => double.NaN,
        1 => 0,
        2 => _number,
        _ => JsNumber.FromText(_text),
    };

    /// <summary>JavaScript truthiness (<c>!x</c> is false): a number other than 0 and NaN, a non-empty text.</summary>
    public bool IsTruthy => _kind switch
    {
        2 => _number != 0 && !double.IsNaN(_number),
        3 => _text!.Length > 0,
        _ => false,
    };

    /// <summary><c>String(x)</c> (what an input shows: the typed text, or the number as JavaScript prints it).</summary>
    public override string ToString() => _kind switch
    {
        0 => "undefined",
        1 => "null",
        2 => JsNumber.ToString(_number),
        _ => _text!,
    };

    /// <summary>A parameter default (<c>(x = 0) =&gt;</c>): only <c>undefined</c> takes it.</summary>
    public JsValue OrDefault(double fallback) => IsUndefined ? Of(fallback) : this;
}
