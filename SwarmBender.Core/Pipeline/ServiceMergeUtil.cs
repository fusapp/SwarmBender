using System;
using System.Collections.Generic;
using SwarmBender.Core.Data.Compose;

namespace SwarmBender.Core.Pipeline
{
    internal static class ServiceMergeUtil
    {
        public static void MergeServiceInto(Service target, Service overlay)
        {
            if (overlay is null) return;

            // ---- simple strings (overlay wins if non-null) ----
            target.Image           = overlay.Image           ?? target.Image;
            target.User            = overlay.User            ?? target.User;
            target.WorkingDir      = overlay.WorkingDir      ?? target.WorkingDir;
            target.StopGracePeriod = overlay.StopGracePeriod ?? target.StopGracePeriod;
            target.StopSignal      = overlay.StopSignal      ?? target.StopSignal;

            // ---- ListOrString (replace if provided) ----
            target.Command    = overlay.Command    ?? target.Command;
            target.Entrypoint = overlay.Entrypoint ?? target.Entrypoint;
            target.EnvFile    = overlay.EnvFile    ?? target.EnvFile;
            target.Dns        = overlay.Dns        ?? target.Dns;
            target.DnsSearch  = overlay.DnsSearch  ?? target.DnsSearch;

            // ---- List<string> (replace if non-empty) ----
            if (overlay.Devices  is { Count: > 0 }) target.Devices  = new(overlay.Devices);
            if (overlay.Tmpfs    is { Count: > 0 }) target.Tmpfs    = new(overlay.Tmpfs);
            if (overlay.CapAdd   is { Count: > 0 }) target.CapAdd   = new(overlay.CapAdd);
            if (overlay.CapDrop  is { Count: > 0 }) target.CapDrop  = new(overlay.CapDrop);
            if (overlay.Profiles is { Count: > 0 }) target.Profiles = new(overlay.Profiles);
            if (overlay.DnsOpt   is { Count: > 0 }) target.DnsOpt   = new(overlay.DnsOpt);

            // ---- ListOrDict (map-merge; list => replace) ----
            target.Environment = MergeListOrDict(target.Environment, overlay.Environment);
            target.Labels      = MergeListOrDict(target.Labels,      overlay.Labels);

            // ---- logging (field-wise) ----
            if (overlay.Logging is not null)
            {
                target.Logging ??= new Logging();
                target.Logging.Driver = overlay.Logging.Driver ?? target.Logging.Driver;

                if (overlay.Logging.Options is { Count: > 0 })
                {
                    target.Logging.Options ??= new(StringComparer.OrdinalIgnoreCase);
                    foreach (var (k, v) in overlay.Logging.Options)
                        target.Logging.Options[k] = v;
                }
            }

            // ---- healthcheck (field-wise) ----
            if (overlay.Healthcheck is not null)
            {
                target.Healthcheck ??= new Healthcheck();
                target.Healthcheck.Test        = overlay.Healthcheck.Test        ?? target.Healthcheck.Test;
                target.Healthcheck.Interval    = overlay.Healthcheck.Interval    ?? target.Healthcheck.Interval;
                target.Healthcheck.Timeout     = overlay.Healthcheck.Timeout     ?? target.Healthcheck.Timeout;
                target.Healthcheck.StartPeriod = overlay.Healthcheck.StartPeriod ?? target.Healthcheck.StartPeriod;
                target.Healthcheck.Retries     = overlay.Healthcheck.Retries     ?? target.Healthcheck.Retries;
            }

            // ---- deploy (labels deep-merge + update_config/restart_policy field-wise) ----
            if (overlay.Deploy is not null)
            {
                target.Deploy ??= new Deploy();

                // labels
                target.Deploy.Labels = MergeListOrDict(target.Deploy.Labels, overlay.Deploy.Labels);

                // replicas (varsa)
                if (overlay.Deploy.Replicas.HasValue)
                    target.Deploy.Replicas = overlay.Deploy.Replicas;

                // update_config
                if (overlay.Deploy.UpdateConfig is not null)
                {
                    target.Deploy.UpdateConfig ??= new UpdateConfig();
                    var s = target.Deploy.UpdateConfig;
                    var o = overlay.Deploy.UpdateConfig;
                    if (o.Parallelism.HasValue) s.Parallelism = o.Parallelism;
                    s.Delay = o.Delay ?? s.Delay;
                    s.Order = o.Order ?? s.Order;
                }

                // restart_policy
                if (overlay.Deploy.RestartPolicy is not null)
                {
                    target.Deploy.RestartPolicy ??= new RestartPolicy();
                    var s = target.Deploy.RestartPolicy;
                    var o = overlay.Deploy.RestartPolicy;
                    s.Condition   = o.Condition   ?? s.Condition;
                    s.Delay       = o.Delay       ?? s.Delay;
                    if (o.MaxAttempts.HasValue) s.MaxAttempts = o.MaxAttempts;
                    s.Window      = o.Window      ?? s.Window;
                }

                // resources vb. varsa aynı kalıpla eklenebilir
            }

            // ---- volumes/ports/secrets/configs (replace if non-empty) ----
            if (overlay.Volumes is { Count: > 0 }) target.Volumes = new(overlay.Volumes);
            if (overlay.Ports   is { Count: > 0 }) target.Ports   = new(overlay.Ports);
            if (overlay.Secrets is { Count: > 0 }) target.Secrets = new(overlay.Secrets);
            if (overlay.Configs is { Count: > 0 }) target.Configs = new(overlay.Configs);

            // ---- networks (short/long) replace if provided ----
            target.Networks = overlay.Networks ?? target.Networks;
        }

        private static ListOrDict? MergeListOrDict(ListOrDict? baseVal, ListOrDict? over)
        {
            if (over is null) return baseVal;
            if (baseVal is null) return over;

            if (over.AsMap is not null)
            {
                baseVal.AsMap ??= new(StringComparer.OrdinalIgnoreCase);
                foreach (var (k, v) in over.AsMap)
                    baseVal.AsMap[k] = v;
                return baseVal;
            }

            // overlay bir liste veriyorsa doğrudan replace
            if (over.AsList is not null) return over;

            return baseVal;
        }
    }
}