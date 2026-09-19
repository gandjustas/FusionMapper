using CleanArchitecture.Application.TodoLists.Queries.GetTodos;
using CleanArchitecture.Domain.Entities;
using CleanArchitecture.Domain.Enums;
using CleanArchitecture.Domain.ValueObjects;
using FusionMapper;
using NUnit.Framework;
using Shouldly;

namespace CleanArchitecture.Application.UnitTests.Common.Mappings;

public class MappingTests
{
    [Test]
    public void ShouldMapTodoItemToTodoItemDto()
    {
        var item = new TodoItem
        {
            Id = 5,
            ListId = 1,
            Title = "Milk",
            Note = "2 liters",
            Priority = PriorityLevel.Medium,
            Done = false
        };

        var dto = item.Map().To<TodoItemDto>();

        dto.Id.ShouldBe(5);
        dto.ListId.ShouldBe(1);
        dto.Title.ShouldBe("Milk");
        dto.Note.ShouldBe("2 liters");
        dto.Done.ShouldBeFalse();
        dto.Priority.ShouldBe((int)PriorityLevel.Medium);
    }

    [Test]
    public void ShouldMapTodoListToTodoListDto()
    {
        var list = new TodoList
        {
            Id = 1,
            Title = "Groceries",
            Colour = Colour.Red
        };
        list.Items.Add(new TodoItem
        {
            Id = 10,
            ListId = 1,
            Title = "Apples",
            Note = "Green ones",
            Priority = PriorityLevel.High,
            Done = true
        });
        list.Items.Add(new TodoItem
        {
            Id = 11,
            ListId = 1,
            Title = "Bread",
            Priority = PriorityLevel.Low
        });

        var dto = list.Map().To<TodoListDto>();

        dto.Id.ShouldBe(1);
        dto.Title.ShouldBe("Groceries");
        dto.Colour.ShouldBe(Colour.Red.ToString());
        dto.Items.Count.ShouldBe(2);

        var apples = dto.Items.Single(i => i.Id == 10);
        apples.Title.ShouldBe("Apples");
        apples.ListId.ShouldBe(1);
        apples.Note.ShouldBe("Green ones");
        apples.Done.ShouldBeTrue();
        apples.Priority.ShouldBe((int)PriorityLevel.High);

        var bread = dto.Items.Single(i => i.Id == 11);
        bread.Title.ShouldBe("Bread");
        bread.Done.ShouldBeFalse();
        bread.Priority.ShouldBe((int)PriorityLevel.Low);
    }
}
