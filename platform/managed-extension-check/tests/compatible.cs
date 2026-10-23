using System;
using System.Reflection;

[assembly:AssemblyVersion("1.0.0.0")]

[AttributeUsage(AttributeTargets.Field)]
public sealed class PersistAttribute : Attribute {
    public PersistAttribute(int id) { Id = id; }
    public int Id { get; private set; }
}

public interface IContract {
    int Read();
}

public class Contract : IContract {
    [Persist(7)]
    public int Value;
    public static int Count;
    public int Read() { return 2; }
    public T Keep<T>(T value)
        where T : class { return value; }
    public string AddedMethod() { return "new"; }
}

public class RemovedType {
    public int StableField;
}
public class AddedType {
    public long NewField;
}
