using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TestProject;

// record 类型（C# 9+）
public record Person(string Name, int Age)
{
    public string Formatted => $"{Name} ({Age})";
}

// record struct（C# 10+）
public readonly record struct Point(double X, double Y);

// 接口 + 默认接口方法（C# 8+）
public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(int id);
    Task<IEnumerable<T>> GetAllAsync();

    // 默认接口方法
    async Task<T?> GetByIdOrDefaultAsync(int id)
    {
        var item = await GetByIdAsync(id);
        return item ?? default;
    }
}

// 实现类：泛型、异步、实现接口
public class UserRepository : IRepository<Person>
{
    private readonly List<Person> _people = new();

    public void Add(Person person) => _people.Add(person);

    public Task<Person?> GetByIdAsync(int id)
    {
        // pattern matching（C# 9+）
        return Task.FromResult(_people.FirstOrDefault(p => p.Age == id));
    }

    public Task<IEnumerable<Person>> GetAllAsync() => Task.FromResult(_people.AsEnumerable());
}

// static using + 方法组
public static class Calculators
{
    // local function + ref return
    private static int[] _buffer = new int[10];

    public static ref int GetSlot(int index) => ref _buffer[index];

    // switch expression（C# 8+）
    public static string ClassifyAge(int age) => age switch
    {
        < 0 => "Invalid",
        < 18 => "Minor",
        < 65 => "Adult",
        >= 65 => "Senior",
    };

    // nullable reference type（C# 8+）
    public static string? TryGetName(Person? person)
    {
        // null-coalescing + null-conditional
        return person?.Name ?? "Unknown";
    }
}

// JSON 序列化（Newtonsoft.Json NuGet 引用）
public class JsonSerializer
{
    public string Serialize(Person person)
    {
        // 使用 NuGet 包的 API——如果项目没正确加载，这里会报 CS0246
        return JsonConvert.SerializeObject(person);
    }

    public Person? Deserialize(string json)
    {
        return JsonConvert.DeserializeObject<Person>(json);
    }
}

// file-scoped namespace（C# 10+）——已在文件顶部声明

// required 成员 + init（C# 11+）
public class Configuration
{
    public required string ConnectionString { get; init; }
    public required int Timeout { get; init; }

    // collection expression（C# 12+）
    public int[] DefaultPorts { get; init; } = [80, 443, 8080];
}
