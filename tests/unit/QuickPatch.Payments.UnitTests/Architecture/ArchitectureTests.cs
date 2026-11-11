using System.Reflection;

using NetArchTest.Rules;

using QuickPatch.Payments.Api.Endpoints;
using QuickPatch.Payments.Application.Payments;
using QuickPatch.Payments.Domain.Payments;
using QuickPatch.Payments.Infrastructure.Persistence;

namespace QuickPatch.Payments.UnitTests.Architecture;

/// <summary>
/// Reglas de dependencia entre capas del SDD 10.3.2 (SCRUM-648). Revisan lo que el compilador no ve:
/// el API referencia a Infrastructure para la composición, pero solo <c>Program</c> puede usarla.
/// </summary>
public sealed class ArchitectureTests
{
    private const string ApiNs = "QuickPatch.Payments.Api";
    private const string ApplicationNs = "QuickPatch.Payments.Application";
    private const string InfrastructureNs = "QuickPatch.Payments.Infrastructure";

    private static readonly string[] Tecnologia =
    [
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "Confluent.Kafka",
        "Microsoft.AspNetCore",
    ];

    private static readonly Assembly Domain = typeof(Payment).Assembly;
    private static readonly Assembly Application = typeof(PayHandler).Assembly;
    private static readonly Assembly Infrastructure = typeof(PaymentsDbContext).Assembly;
    private static readonly Assembly Api = typeof(PaymentEndpoints).Assembly;

    [Fact]
    public void Domain_NoDependeDeOtrasCapasNiDeTecnologia() =>
        AssertOk(Types.InAssembly(Domain).ShouldNot()
            .HaveDependencyOnAny([ApplicationNs, InfrastructureNs, ApiNs, .. Tecnologia]).GetResult());

    [Fact]
    public void Application_NoDependeDeInfrastructureApiNiTecnologia() =>
        AssertOk(Types.InAssembly(Application).ShouldNot()
            .HaveDependencyOnAny([InfrastructureNs, ApiNs, .. Tecnologia]).GetResult());

    [Fact]
    public void Infrastructure_NoDependeDelApi() =>
        AssertOk(Types.InAssembly(Infrastructure).ShouldNot()
            .HaveDependencyOnAny(ApiNs, "Microsoft.AspNetCore").GetResult());

    [Fact]
    public void Api_SoloLaRaizDeComposicionConoceInfrastructure() =>
        AssertOk(Types.InAssembly(Api).That().DoNotHaveName("Program")
            .ShouldNot().HaveDependencyOnAny(InfrastructureNs).GetResult());

    [Fact]
    public void Endpoints_NoUsanPersistenciaNiMensajeria() =>
        AssertOk(Types.InAssembly(Api).That().ResideInNamespace($"{ApiNs}.Endpoints")
            .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Npgsql", "Confluent.Kafka").GetResult());

    private static void AssertOk(TestResult result) =>
        Assert.True(
            result.IsSuccessful,
            "Viola SDD 10.3.2: " + string.Join(", ", result.FailingTypes?.Select(t => t.FullName) ?? []));
}