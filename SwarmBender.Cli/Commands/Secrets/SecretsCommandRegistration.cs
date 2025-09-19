using Spectre.Console.Cli;

namespace SwarmBender.Cli.Commands.Secrets;

public static class SecretsCommandRegistration
{
    public static void AddSecretsCommands(this IConfigurator cfg)
    {
        cfg.AddBranch("secrets", b =>
        {
            b.SetDescription("Manage Swarm secrets lifecycle (sync/prune/doctor).");
            b.AddCommand<SecretsSyncCommand>("sync").WithDescription("Sync secrets from providers into Docker Swarm and write secrets map.");
            b.AddCommand<SecretsPruneCommand.Exec>("prune").WithDescription("Prune old secret versions based on retain policy.");
            b.AddCommand<SecretsDoctorCommand.Exec>("doctor").WithDescription("Check consistency between secrets-map and Docker Engine.");
            b.AddCommand<SecretsRotateCommand>("rotate");
        });
    }
}