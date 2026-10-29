namespace ClipStack.Core.Models;

public enum ClipType
{
    Text = 0,
    Image = 1,
    File = 2,
}

public static class ClipTypeNames
{
    public static string ToDbValue(this ClipType type) => type switch
    {
        ClipType.Text => "text",
        ClipType.Image => "image",
        ClipType.File => "file",
        _ => "text",
    };

    public static ClipType FromDbValue(string value) => value switch
    {
        "image" => ClipType.Image,
        "file" => ClipType.File,
        _ => ClipType.Text,
    };
}
