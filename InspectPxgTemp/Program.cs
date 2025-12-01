using System;
using System.Reflection;
using MagicPhysX;

static void DumpType(string name, Type type)
{
	Console.WriteLine($"Type {name}");
	foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
		Console.WriteLine($"  field: {field.Name} ({field.FieldType.Name})");
	foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
		Console.WriteLine($"  property: {prop.Name} ({prop.PropertyType.Name})");
}

DumpType(nameof(PxgDynamicsMemoryConfig), typeof(PxgDynamicsMemoryConfig));

var config = new PxgDynamicsMemoryConfig();
Console.WriteLine();
Console.WriteLine("Default values:");
foreach (var field in typeof(PxgDynamicsMemoryConfig).GetFields(BindingFlags.Public | BindingFlags.Instance))
{
	Console.WriteLine($"  {field.Name} = {field.GetValue(config)}");
}
