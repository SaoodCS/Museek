using Museek.Core;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++;
}

var selection = new TrimSelection(10);
Check(selection.Start == 0 && selection.End == 10, "Initially selects the entire song.");
selection.SetStart(12);
Check(selection.Start < selection.End && selection.Start >= 0, "Start cannot cross the end.");
selection.SetEnd(-4);
Check(selection.End > selection.Start && selection.End <= 10, "End cannot cross the start.");
selection.Reset(3);
selection.SetStart(-1);
selection.SetEnd(9);
Check(selection.Start == 0 && selection.End == 3, "Endpoints are clamped to the song.");
selection.Reset(0.01);
Check(selection.End == 0.01 && selection.MinimumLength <= 0.01, "Very short media remains trimmable.");
selection.SetStart(1);
Check(selection.Start >= 0 && selection.Start < selection.End, "Very short media maintains a positive selection.");
selection.Reset(0);
Check(selection.Start == 0 && selection.End == 0, "Empty media has no selection.");
selection.Reset(10);
selection.SetStart(double.NaN);
selection.SetEnd(double.PositiveInfinity);
Check(double.IsFinite(selection.Start) && double.IsFinite(selection.End), "Non-finite input cannot corrupt range state.");
Console.WriteLine($"PASS: {checks} trim-selection checks.");
