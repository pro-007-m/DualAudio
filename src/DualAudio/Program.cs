using DualAudio.Audio;

namespace DualAudio;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            bool startImmediately = args.Length == 1 && args[0] == "--start";
            if (args.Length > 0 && !startImmediately) return Diagnostics.Run(args);
            using var instance = new Mutex(true, "Local\\DualAudio-98876F54", out bool first);
            if (!first)
            {
                MessageBox.Show("Приложение уже открыто. Проверьте значок наушников рядом с часами.",
                    "Два наушника", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message,
                "Два наушника — ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            var form = new MainForm();
            if (startImmediately) form.Shown += (_, _) => form.BeginInvoke(form.StartFromCommandLine);
            Application.Run(form);
            return 0;
        }
        catch (Exception e)
        {
            if (args.Length > 0)
            {
                if (args.Length > 1) File.WriteAllText(args[1], e.ToString());
                return 1;
            }
            MessageBox.Show(e.Message, "Два наушника", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
