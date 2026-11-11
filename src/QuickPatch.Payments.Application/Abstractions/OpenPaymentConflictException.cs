using QuickPatch.Payments.Application.Invoices;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Abstractions;

/// <summary>El índice único de pagos abiertos rechazó un segundo pago <c>pendiente</c> o <c>procesando</c> (RN-P5).</summary>
public sealed class OpenPaymentConflictException : Exception
{
    public OpenPaymentConflictException()
        : base("La solicitud ya tiene un pago abierto.")
    {
    }

    public OpenPaymentConflictException(string message)
        : base(message)
    {
    }

    public OpenPaymentConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}