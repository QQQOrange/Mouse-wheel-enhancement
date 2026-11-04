import io
p = r"D:\Local AI\git\Mouse wheel enhancement源代码\App.xaml.cs"
s = io.open(p, encoding="utf-8").read()
anchor = '''        if (e.Args.Any(a => a.Equals("--wpfprobe", StringComparison.OrdinalIgnoreCase)))'''
assert anchor in s
new = '''        if (e.Args.Any(a => a.Equals("--keyprobe", StringComparison.OrdinalIgnoreCase)))
        {
            Services.Init();
            var keyCode = KeyProbe();
            Shutdown(keyCode);
            Environment.Exit(keyCode);
            return;
        }

''' + anchor
s = s.replace(anchor, new, 1)
io.open(p, "w", encoding="utf-8", newline="").write(s)
print("ok2")