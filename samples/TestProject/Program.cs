using System;
using System.Threading.Tasks;
using TestProject;

// top-level statements（C# 9+）——程序入口
// 注意：顶层语句文件不能有 namespace 声明

var person = new Person("Alice", 30);
Console.WriteLine($"Hello, {person.Formatted}!");

// 调用各种语法特性
var repo = new UserRepository();
repo.Add(person);
repo.Add(new Person("Bob", 25));

var found = await repo.GetByIdAsync(30);
Console.WriteLine($"Found: {found?.Name ?? "nobody"}");

// pattern matching
Console.WriteLine(Calculators.ClassifyAge(person.Age));

// JSON 序列化（验证 NuGet 引用解析）
var serializer = new JsonSerializer();
var json = serializer.Serialize(person);
Console.WriteLine($"JSON: {json}");

// ref return（在顶层语句中简化使用）
var slotVal = Calculators.GetSlot(0);
Console.WriteLine($"Slot value: {slotVal}");

// required + init + collection expression
var config = new Configuration
{
    ConnectionString = "Server=localhost",
    Timeout = 30,
};
Console.WriteLine($"Ports: {string.Join(", ", config.DefaultPorts)}");
