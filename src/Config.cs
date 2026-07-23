using System;
using System.IO;
using System.Web.Script.Serialization;

namespace MiniFan
{
    public class Config
    {
        // Seuils d'activation (degres C). Le boost s'allume si CPU >= CpuOn OU GPU >= GpuOn,
        // et s'eteint quand CPU <= CpuOff ET GPU <= GpuOff (hysteresis) apres MinBoostSeconds.
        public int CpuOn = 75;
        public int CpuOff = 65;
        public int GpuOn = 70;
        public int GpuOff = 60;
        public int MinBoostSeconds = 60;
        public int PollSeconds = 3;

        // auto | boost | silent
        public string Mode = "auto";

        // Detection de jeux : boost immediat des qu'un de ces process tourne.
        public bool GameBoost = true;
        public string Games = "FortniteClient-Win64-Shipping, VALORANT-Win64-Shipping, cs2, javaw, Minecraft, GTA5, RocketLeague, r5apex, eldenring, RustClient, RainbowSix, overwatch";

        // Mise a jour automatique via releases GitHub.
        public bool AutoUpdate = true;
        // NOTE SECURITE : conserve pour compat de deserialisation uniquement. Le depot
        // source des MAJ est desormais FIGE a la compilation (Updater.OfficialRepo) et
        // ce champ n'influence PLUS l'URL de mise a jour (evite une elevation de
        // privileges via un config.json modifie).
        public string UpdateRepo = "saliox/mini-fan";

        private static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"); }
        }

        public static Config Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var ser = new JavaScriptSerializer();
                    var cfg = ser.Deserialize<Config>(File.ReadAllText(FilePath));
                    if (cfg != null)
                    {
                        // Si Sanitize() a dû corriger une valeur corrompue/hors bornes, on
                        // réécrit le fichier tout de suite : sinon la réparation ne vit qu'en
                        // mémoire et le fichier sur disque reste corrompu indéfiniment (il sera
                        // "réparé" à chaque démarrage sans jamais être vraiment corrigé).
                        if (cfg.Sanitize()) cfg.Save();
                        return cfg;
                    }
                }
            }
            catch { }
            var fresh = new Config();
            fresh.Save();
            return fresh;
        }

        public void Save()
        {
            try
            {
                var ser = new JavaScriptSerializer();
                File.WriteAllText(FilePath, ser.Serialize(this));
            }
            catch { }
        }

        /// <summary>Corrige en mémoire les valeurs corrompues/hors bornes. Retourne true si
        /// au moins une valeur a effectivement été modifiée (l'appelant doit alors persister
        /// la correction via Save(), sans quoi le fichier sur disque reste corrompu).</summary>
        private bool Sanitize()
        {
            int origCpuOn = CpuOn, origGpuOn = GpuOn, origCpuOff = CpuOff, origGpuOff = GpuOff,
                origPollSeconds = PollSeconds, origMinBoostSeconds = MinBoostSeconds;
            string origMode = Mode;

            if (CpuOn < 40) CpuOn = 40; if (CpuOn > 95) CpuOn = 95;
            if (GpuOn < 40) GpuOn = 40; if (GpuOn > 95) GpuOn = 95;
            CpuOff = CpuOn - 10;
            GpuOff = GpuOn - 10;
            if (PollSeconds < 2) PollSeconds = 2;
            if (PollSeconds > 30) PollSeconds = 30;
            if (MinBoostSeconds < 15) MinBoostSeconds = 15;
            // Plafond : sans borne haute, une valeur enorme empeche le boost de se couper
            // (ventilateurs a fond en permanence).
            if (MinBoostSeconds > 3600) MinBoostSeconds = 3600;
            if (Mode != "auto" && Mode != "boost" && Mode != "silent") Mode = "auto";

            return origCpuOn != CpuOn || origGpuOn != GpuOn || origCpuOff != CpuOff ||
                   origGpuOff != GpuOff || origPollSeconds != PollSeconds ||
                   origMinBoostSeconds != MinBoostSeconds || origMode != Mode;
        }
    }
}
