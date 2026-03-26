using System;
using System.Reflection;
var asm = Assembly.LoadFrom(@"C:\Users\alvin\AppData\Roaming\XIVLauncher\addon\Hooks\dev\FFXIVClientStructs.dll");
var type = asm.GetType("FFXIVClientStructs.FFXIV.Component.GUI.AtkImageNode");
if (type == null) { Console.WriteLine("Type not found"); return; }
Console.WriteLine("=== AtkImageNode Fields ===");
foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
{
    var offset = "";
    foreach (var attr in f.GetCustomAttributesData())
        if (attr.AttributeType.Name == "FieldOffsetAttribute")
            offset = $" [Offset: 0x{attr.ConstructorArguments[0].Value:X}]";
    Console.WriteLine($"  {f.FieldType.Name,-30} {f.Name}{offset}");
}
Console.WriteLine("\n=== AtkImageNode Methods ===");
foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
    Console.WriteLine($"  {m.ReturnType.Name,-20} {m.Name}({string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name + " " + p.Name))})");
