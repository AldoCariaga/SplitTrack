using System.Text;

namespace SplitTrack.VideoPoc.Recording;

/// <summary>Redraws a block of lines in place on a console, or prints every 5 s when redirected.</summary>
internal sealed class StatusDisplay(bool interactive)
{
    private int _top = -1;
    private int _renders;

    public void Render(List<string> lines)
    {
        if (!interactive)
        {
            if (_renders++ % 5 == 0)
            {
                lines.ForEach(Console.WriteLine);
            }

            return;
        }

        int width = Math.Max(20, Console.WindowWidth - 1);
        var block = new StringBuilder();
        foreach (string line in lines)
        {
            block.AppendLine(line.Length > width ? line[..width] : line.PadRight(width));
        }

        if (_top >= 0)
        {
            Console.SetCursorPosition(0, _top);
        }

        Console.Write(block);
        _top = Console.CursorTop - lines.Count;
    }
}
