using System;
using System.Reflection;
using System.Windows.Forms;

internal static class SelectableWarningTest
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
            return Fail("Expected the path to DeltaTranslator.exe.");

        Assembly assembly = Assembly.LoadFrom(args[0]);
        Type form = assembly.GetType("DeltaTranslatorForm", true);
        MethodInfo factory = form.GetMethod(
            "NewRunWarningTextBox",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (factory == null)
            return Fail("The warning TextBox factory was not found.");

        using (TextBox warning = factory.Invoke(null, null) as TextBox)
        {
            if (warning == null)
                return Fail("The warning control is not a TextBox.");
            if (!warning.ReadOnly || !warning.Multiline || !warning.ShortcutsEnabled)
                return Fail("The warning TextBox is not configured for safe text selection.");
            if (warning.BorderStyle != BorderStyle.None)
                return Fail("The warning TextBox no longer matches the label-like layout.");

            warning.Text = "WARNING: unsupported characters: 'Ć'x2";
            int index = warning.Text.IndexOf('Ć');
            warning.Select(index, 1);
            if (warning.SelectedText != "Ć")
                return Fail("A warning character cannot be selected.");
        }

        Console.WriteLine("Selectable warning control: OK");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
