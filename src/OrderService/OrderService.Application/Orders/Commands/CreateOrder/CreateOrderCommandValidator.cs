using FluentValidation;

namespace OrderService.Application.Orders.Commands.CreateOrder;

/// <summary>
/// Declarative validation rules for CreateOrderCommand.
/// The ValidationBehavior runs this automatically before the handler —
/// the handler can safely assume the command is well-formed.
/// </summary>
public sealed class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderCommandValidator()
    {
        RuleFor(c => c.UserId).NotEmpty();

        RuleFor(c => c.Items)
            .NotEmpty().WithMessage("An order must contain at least one item.");

        // RuleForEach applies the child rules to EVERY element of the list.
        RuleForEach(c => c.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).NotEmpty().MaximumLength(64);
            item.RuleFor(i => i.ProductName).NotEmpty().MaximumLength(256);
            item.RuleFor(i => i.Quantity)
                .GreaterThan(0).WithMessage("Quantity must be at least 1.");
            item.RuleFor(i => i.UnitPrice)
                .GreaterThanOrEqualTo(0).WithMessage("Unit price cannot be negative.");
        });
    }
}
