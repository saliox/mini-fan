# Mini Fan 🌀

Contrôle automatique du **Cooler Boost** (ventilateurs à fond, l'équivalent de FN+↑)
sur portable MSI. Petite app de barre des tâches (~50 Mo de RAM, ~0 % CPU), aucune dépendance.

## Ce que ça fait

- Lit les températures CPU/GPU directement dans l'Embedded Controller MSI
  (registres `0x68` / `0x80`), via l'interface WMI `MSI_ACPI` du BIOS — **aucun pilote tiers**.
- Active le Cooler Boost (registre `0x98`, bit 7) quand :
  - le CPU dépasse **75 °C** ou le GPU **70 °C** (réglable dans l'app), **ou**
  - un **jeu** de la liste est lancé (Fortnite, Valorant, Minecraft… liste modifiable).
- Le désactive avec hystérésis (CPU ≤ 65 °C **et** GPU ≤ 60 °C, minimum 60 s de boost)
  pour éviter le yo-yo des ventilateurs.
- Trois modes : **Auto** / **Boost** permanent / **Repos** (jamais de boost).
- Se met à jour **toute seule** depuis les releases GitHub de ce dépôt.
- Démarre avec Windows (tâche planifiée élevée, active sur batterie).

## Installation (sur le portable MSI)

PowerShell **en administrateur** :

```powershell
irm https://raw.githubusercontent.com/saliox/mini-fan/main/install.ps1 | iex
```

C'est tout : l'app est installée dans `%LOCALAPPDATA%\MiniFan`, démarre avec Windows
et apparaît en icône ventilateur à côté de l'horloge.

## Publier une mise à jour (depuis la tour)

```powershell
.\publish-update.ps1 -Version 1.1.0 -Notes "Ce qui change"
```

Le portable installe la nouvelle version tout seul (vérification au démarrage puis toutes les 6 h).

## Si le pilotage ne marche pas sur ton modèle

Ouvre l'app → clique **Diagnostic** → le rapport est copié dans le presse-papiers :
colle-le dans la conversation Claude et j'adapterai la sonde (certains vieux modèles
MSI « WMI1 » ont une interface différente).

## Build

```powershell
.\build.ps1     # csc natif .NET Framework 4.8, sortie dans build\MiniFan.exe
```

## Références

- Registres EC MSI : [BeardOverflow/msi-ec](https://github.com/BeardOverflow/msi-ec)
- Interface WMI : [doc kernel Linux msi-wmi-platform](https://docs.kernel.org/wmi/devices/msi-wmi-platform.html)
