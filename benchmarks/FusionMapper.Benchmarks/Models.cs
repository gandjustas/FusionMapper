// --- Модели для бенчмарка ---
public class SimpleSource
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
}

public class SimpleDestination
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
}

public class NestedSource
{
    public string Name { get; set; } = string.Empty;
    public Level1 Level1 { get; set; } = new();
}

public class Level1
{
    public string Title { get; set; } = string.Empty;
    public Level2 Level2 { get; set; } = new();
}

public class Level2
{
    public string Description { get; set; } = string.Empty;
    public int Value { get; set; }
}

public class NestedDestination
{
    public string Name { get; set; } = string.Empty;
    public string Level1Title { get; set; } = string.Empty;
    public string Level1Level2Description { get; set; } = string.Empty;
    public int Level1Level2Value { get; set; }
}

public class CollectionSource
{
    public List<SimpleSource> Items { get; set; } = [];
}

public class CollectionDestination
{
    public List<SimpleDestination> Items { get; set; } = [];
}