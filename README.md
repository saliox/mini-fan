# Mini Fan 🌀

Le Cooler Boost de ton portable MSI (les ventilateurs à fond, l'équivalent de FN+↑), mais déclenché
tout seul quand il faut. Petite appli dans la barre des tâches : ~50 Mo de RAM, quasiment pas de CPU,
aucune dépendance à installer.

## Comment ça marche

Mini Fan lit les températures CPU et GPU **directement dans l'Embedded Controller** de MSI, via
l'interface WMI `MSI_ACPI` du BIOS. Aucun pilote tiers, rien à installer à côté.

Il met les ventilateurs à fond quand :

- le CPU dépasse **75 °C** ou le GPU **70 °C** (les deux sont réglables), **ou**
- un **jeu** de ta liste se lance (Fortnite, Valorant, Minecraft… la liste est modifiable).

Et il les redescend quand le CPU repasse sous 65 °C **et** le GPU sous 60 °C, avec un minimum de 60 s
de boost — sinon les ventilateurs feraient du yo-yo à chaque pic de température.

Trois modes selon ton humeur : **Auto**, **Boost** permanent, ou **Repos** (jamais de boost).

L'appli démarre avec Windows et se met à jour toute seule depuis les releases de ce dépôt.

## Installation

Sur le portable MSI, dans un PowerShell **administrateur** :

```powershell
irm https://raw.githubusercontent.com/saliox/mini-fan/main/install.ps1 | iex
```

C'est tout. L'appli s'installe dans `%LOCALAPPDATA%\MiniFan`, démarre avec Windows, et apparaît en
icône ventilateur à côté de l'horloge.

L'installeur **vérifie le binaire avant de l'installer** : il compare son empreinte SHA-256 à celle
publiée avec la release, et refuse d'installer quoi que ce soit qui ne corresponde pas.

## Si ça ne pilote pas sur ton modèle

Tous les portables MSI n'exposent pas leur contrôleur de la même façon. Ouvre l'appli, clique
**Diagnostic** : le rapport est copié dans ton presse-papiers. Colle-le dans une
[issue](../../issues) et la sonde pourra être adaptée à ton modèle.

Par sécurité, Mini Fan ne s'autorise à écrire dans le contrôleur qu'après avoir vérifié qu'une écriture
neutre se comporte comme attendu. Si ce test échoue, il passe en lecture seule plutôt que de risquer
d'écrire au mauvais endroit.

## Publier une mise à jour

Depuis la machine de développement :

```powershell
.\publish-update.ps1 -Version 1.1.0 -Notes "Ce qui change"
```

Le script construit, publie la release avec son empreinte SHA-256, et les portables installés récupèrent
la nouvelle version tout seuls — au démarrage, puis toutes les 6 h.

Les mises à jour ne s'installent que si la signature Authenticode du binaire est valide **et** provient
du même signataire que la version en place.

## Compiler

```powershell
.\build.ps1     # csc natif, .NET Framework 4.8 → build\MiniFan.exe
```

## Références

- Registres EC MSI : [BeardOverflow/msi-ec](https://github.com/BeardOverflow/msi-ec)
- Interface WMI : [doc kernel Linux msi-wmi-platform](https://docs.kernel.org/wmi/devices/msi-wmi-platform.html)
