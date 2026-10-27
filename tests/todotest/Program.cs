using System;
using System.IO;
using System.Linq;
using CalendarWidget;

static class P
{
    static int fails;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) fails++; }

    static int Main()
    {
        string dir = Environment.GetEnvironmentVariable("CALENDARWIDGET_DATA");
        if (string.IsNullOrEmpty(dir)) { Console.WriteLine("need CALENDARWIDGET_DATA"); return 2; }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);

        var s = LocalTodoStore.Load();
        Check("empty at start", !s.ToItems("#7EE0A1").Any());

        var a = s.Add(new DateTime(2026, 10, 26), "第一件\t含Tab\n和換行");
        var b = s.Add(new DateTime(2026, 10, 27), "第二件");
        Check("two items", s.ToItems("#7EE0A1").Count() == 2);

        var r = LocalTodoStore.Load();
        var items = r.ToItems("#7EE0A1").ToList();
        Check("reload count", items.Count == 2);
        Check("title sanitized", items.First(i => i.Id == a.Id).Title == "第一件 含Tab 和換行");
        Check("chinese kept", items.First(i => i.Id == b.Id).Title == "第二件");

        r.Toggle(a.Id);
        Check("toggle persisted", LocalTodoStore.Load().ToItems("#7EE0A1").First(i => i.Id == a.Id).Done);
        r.Toggle(a.Id);
        Check("toggle back persisted", !LocalTodoStore.Load().ToItems("#7EE0A1").First(i => i.Id == a.Id).Done);

        r.Remove(b.Id);
        var after = LocalTodoStore.Load().ToItems("#7EE0A1").ToList();
        Check("remove persisted", after.Count == 1 && after[0].Id == a.Id);
        Check("no leftover tmp", !File.Exists(Path.Combine(dir, "todos.txt.tmp")));

        Settings.ReadOnly = true;
        var ro = LocalTodoStore.Load();
        ro.Add(DateTime.Today, "唯讀不應寫入");
        Settings.ReadOnly = false;
        Check("readonly does not write", LocalTodoStore.Load().ToItems("#7EE0A1").Count() == 1);

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails;
    }
}
