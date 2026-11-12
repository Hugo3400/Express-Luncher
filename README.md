<a id="top"></a>

<div align="center">

<img src="assets/banner.svg" alt="Express Luncher — game launcher for Windows" width="100%">

<br><br>

![Status](https://img.shields.io/badge/status-in%20development-ff8a3d?style=for-the-badge)
![Platform](https://img.shields.io/badge/platform-Windows-0078D4?style=for-the-badge)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/UI-WPF-5C2D91?style=for-the-badge)
![C#](https://img.shields.io/badge/C%23-239120?style=for-the-badge)
![Vibe coded](https://img.shields.io/badge/vibe-coded-ff3d77?style=for-the-badge)
[![License: source-available](https://img.shields.io/badge/license-source--available-e5534b?style=for-the-badge)](LICENSE)

[![Stars](https://img.shields.io/github/stars/Hugo3400/Express-Luncher?style=flat-square&color=ff3d77)](https://github.com/Hugo3400/Express-Luncher/stargazers)
[![Issues](https://img.shields.io/github/issues/Hugo3400/Express-Luncher?style=flat-square&color=ff8a3d)](https://github.com/Hugo3400/Express-Luncher/issues)
![Last commit](https://img.shields.io/github/last-commit/Hugo3400/Express-Luncher?style=flat-square&color=512BD4)

### 🇫🇷 [Français](#francais) &nbsp;·&nbsp; 🇬🇧 [English](#english)

</div>

<br>

<a id="francais"></a>

## 🇫🇷 Français

**Express Luncher** est un launcher de jeu pour Windows, développé en **C# / .NET 10** avec **WPF**.
Son but : installer le jeu, le garder à jour et le lancer en un clic. 🚀

> [!NOTE]
> **Projet en tout début de développement.** Le squelette de l'application est en place, mais aucune fonctionnalité n'est encore disponible et aucune version n'est téléchargeable pour le moment.

### ✨ Fonctionnalités prévues

|     | Fonctionnalité | Description |
| :-: | :-- | :-- |
| ▶️ | **Lancement en un clic** | Démarrer le jeu directement depuis le launcher |
| 🔄 | **Mises à jour automatiques** | Téléchargement et installation sans intervention |
| 👤 | **Connexion au compte** | Identification du joueur depuis le launcher |
| 📰 | **Actualités** | Les dernières nouvelles du jeu sur l'écran d'accueil |
| ⚙️ | **Réglages** | Dossier d'installation et options de lancement |

Cette liste décrit l'objectif du projet, pas ce qui existe déjà. Elle évoluera avec le développement.

### 🗺️ Feuille de route

- [x] Création du dépôt
- [x] Structure du projet .NET
- [ ] Interface du launcher
- [ ] Lancement du jeu
- [ ] Système de mise à jour
- [ ] Première version publique

### 📦 Installation

Aucune version n'est encore disponible. Les téléchargements seront publiés dans l'onglet **[Releases](https://github.com/Hugo3400/Express-Luncher/releases)**.

### 🛠️ Compiler depuis les sources

Prérequis : Windows et le [SDK .NET 10](https://dotnet.microsoft.com/download).

```powershell
git clone https://github.com/Hugo3400/Express-Luncher.git
cd Express-Luncher
dotnet build ExpressLauncher.slnx
dotnet run --project src/ExpressLauncher
```

### 🧱 Structure du projet

```text
ExpressLauncher.slnx          Solution
src/
├── ExpressLauncher/          Application WPF (interface)
└── ExpressLauncher.Core/     Bibliothèque de classes (logique, sans interface)
```

### 🤖 Vibe codé

Ce projet est **vibe codé** : il est développé avec l'aide d'une IA, à partir d'instructions en langage naturel. Le code peut donc contenir des imperfections, et les relectures comme les corrections sont les bienvenues.

### 🤝 Contribuer

Les idées, retours, signalements de bugs et corrections sont les bienvenus : ouvrez une **[issue](https://github.com/Hugo3400/Express-Luncher/issues)** ou proposez une **[pull request](https://github.com/Hugo3400/Express-Luncher/pulls)**. Les contributions sont intégrées au projet selon les termes de la [licence](LICENSE).

### 📄 Licence

Le code est **visible, mais pas libre de droits** : tous droits réservés, voir la **[licence](LICENSE)**.

| ✅ Autorisé | ❌ Interdit sans accord écrit |
| :-- | :-- |
| Lire le code | Copier le code dans un autre projet |
| Compiler et utiliser le launcher pour soi | Redistribuer ou revendre le projet, modifié ou non |
| Contribuer : issues, corrections, pull requests | Présenter le projet comme le sien ou sous un autre nom |

<p align="right"><a href="#top">⬆️ Retour en haut</a></p>

---

<a id="english"></a>

## 🇬🇧 English

**Express Luncher** is a game launcher for Windows, built with **C# / .NET 10** and **WPF**.
Its goal: install the game, keep it up to date and start it in one click. 🚀

> [!NOTE]
> **Very early development.** The application skeleton is in place, but no feature is available yet and there is no downloadable release at this time.

### ✨ Planned features

|     | Feature | Description |
| :-: | :-- | :-- |
| ▶️ | **One-click launch** | Start the game straight from the launcher |
| 🔄 | **Automatic updates** | Downloaded and installed without any manual step |
| 👤 | **Account sign-in** | Player login from inside the launcher |
| 📰 | **News feed** | The latest game news on the home screen |
| ⚙️ | **Settings** | Install folder and launch options |

This list describes where the project is headed, not what exists today. It will change as development moves forward.

### 🗺️ Roadmap

- [x] Repository created
- [x] .NET project structure
- [ ] Launcher interface
- [ ] Game launch
- [ ] Update system
- [ ] First public release

### 📦 Installation

No release is available yet. Downloads will be published under **[Releases](https://github.com/Hugo3400/Express-Luncher/releases)**.

### 🛠️ Building from source

Requirements: Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
git clone https://github.com/Hugo3400/Express-Luncher.git
cd Express-Luncher
dotnet build ExpressLauncher.slnx
dotnet run --project src/ExpressLauncher
```

### 🧱 Project structure

```text
ExpressLauncher.slnx          Solution
src/
├── ExpressLauncher/          WPF application (user interface)
└── ExpressLauncher.Core/     Class library (logic, no UI)
```

### 🤖 Vibe coded

This project is **vibe coded**: it is built with the help of an AI, from natural-language instructions. The code may therefore have rough edges, and reviews and fixes are welcome.

### 🤝 Contributing

Ideas, feedback, bug reports and fixes are welcome: open an **[issue](https://github.com/Hugo3400/Express-Luncher/issues)** or submit a **[pull request](https://github.com/Hugo3400/Express-Luncher/pulls)**. Contributions become part of the project under the terms of the [license](LICENSE).

### 📄 License

The code is **source-available, not open source**: all rights reserved, see the **[license](LICENSE)**.

| ✅ Allowed | ❌ Not allowed without written permission |
| :-- | :-- |
| Read the code | Copy the code into another project |
| Build and run the launcher for yourself | Redistribute or sell the project, modified or not |
| Contribute: issues, fixes, pull requests | Present the project as your own or under another name |

<p align="right"><a href="#top">⬆️ Back to top</a></p>

---

<div align="center">

Vibe coded with ❤️ by <a href="https://github.com/Hugo3400">Hugo3400</a>

</div>
