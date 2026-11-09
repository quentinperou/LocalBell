# Microsoft Store listing

What goes into Partner Center for **LocalBell**, page by page, ready to paste (as NowPlaying's). Kept here so that the
next submissions start from it. The package: `dist\store\LocalBell_<version>.0_x64.msix` (`scripts\build-store.ps1`).

The name "LocalBell" is reserved in Partner Center (the same account as NowPlaying, November 9, 2026); its Product
identity is the one of `store/AppxManifest.xml`: Name `QuentinPerou.LocalBell`, Publisher
`CN=E4BBF1EB-8886-49FD-8647-36786CAF5AC5`, PublisherDisplayName `QuentinPerou`.

## Pricing and availability

- Markets: all. Visibility: public. Price: free (no trial, no add-ons).
- Publish: as soon as it passes certification.

## Properties

- Category: **Productivity**. Secondary category: **Business**.
- Does the app access, collect or transmit personal information? **No**: the names of the PCs and the messages go to
  the PCs of the local network only, never to the author nor to the internet.
- Privacy policy URL: `https://github.com/quentinperou/LocalBell#privacy-policy`
- Website: `https://github.com/quentinperou/LocalBell`
- Support contact info: `https://github.com/quentinperou/LocalBell/issues`
- Product declarations: the defaults (install to alternate drives, OneDrive backups); nothing else.
- System requirements: none.

## Age ratings (IARC questionnaire)

Category: utility / productivity / other. Users exchange short messages, between the PCs of one local network only
(no internet, no account, no strangers): answer **Yes** to "users can interact" if the questionnaire asks it, **No**
to everything else (violence, location, purchases, unrestricted web access). Expected rating: 3+ (PEGI 3).

## Packages

Upload the `.msix` (the first one: `LocalBell_0.1.6.0_x64.msix`). Device family: Windows 10/11 Desktop (the package
needs Windows 10 2004).

## Submission options

**Why do you need the runFullTrust capability?** (500 characters at most)

```
Desktop app (WPF, .NET) that rings the other PCs of a local network. Full trust is needed to: find the other PCs and answer them (UDP multicast and broadcast on every network card, a small HTTP server, with the firewall rules of the package); show its popup and a widget window over the taskbar (Win32, UI Automation); turn the sound of Windows back on while it rings (Core Audio); run its startup task. Nothing goes to the internet.
```

**Notes for certification**

```
No account, no sign-in. LocalBell rings the other PCs of the local network that run it: alone on a network it shows "No computer online", which is expected. To try a ring, run it on a second PC of the same network: each one lists the other, "Ring" shows a popup on the other one, its reply comes back on the row. Alone: Settings (from the Start menu or the bell near the clock) > Ring > the play button plays the bell; Settings > Popup shows the two popups.
```

## What's new (each submission, both listings)

**0.1.6**: empty, the first submission.

## Store listing: English (en-us)

**Description**

```
A doorbell between the computers of your shop or office: ring a colleague, with or without a message.

Install LocalBell on each computer and name each one once: every computer of the local network sees the others. Ring one in a click, or everyone in two; ready-made messages ("Customer at the till") are one click too. The computer rung shows a popup in a corner of the screen and rings until someone answers, and the reply ("On my way") comes back on your list.

A widget in the taskbar rings your favourite in one click. "Do not disturb" for 30 minutes, an hour or until tomorrow keeps the rings for later. The bell rings even when the sound of Windows is off.

No server, no account: the computers find each other on the local network, on any network card; behind a router, add one once by its address.

Free and open source (MIT).
```

**Product features** (one per field)

```
Ring a computer of the local network in one click, with or without a message
Ready-made messages and one-click replies
Ring everyone in two clicks
A popup in the corner of your choice, discreet or loud
The bell rings until someone answers, even with the sound of Windows off
Three bells and a music box, or your own WAV or MP3 file, at a volume of its own
Do not disturb: 30 minutes, an hour, until tomorrow; missed rings kept
A taskbar widget: your favourite rung in one click
The history of the rings received and sent, with "Ring back"
Keyboard shortcuts to ring a computer or open LocalBell
Finds the other computers by itself, on any network; by address behind a router
No server, no account, nothing on the internet
Light or dark theme, English and French
```

**Short description**

```
A doorbell between the computers of your shop or office: ring a colleague in one click, with or without a message, and get the reply.
```

**Screenshots**: `.github/hero.png` (1560 × 1000).

**Search terms** (one per field)

```
doorbell
call a colleague
intercom
local network
lan messenger
ring
office bell
```

**Copyright and trademark info**

```
Copyright (c) 2026 QuentinPerou.
```

**Developed by**: `QuentinPerou`. **What's new in this version**: empty for the first submission.

## Store listing: French (fr-fr)

**Description**

```
Une sonnette entre les PC de votre boutique ou de votre bureau : sonnez un collègue, avec ou sans message.

Installez LocalBell sur chaque PC et donnez une fois un nom à chacun : chaque PC du réseau local voit les autres. Sonnez-en un en un clic, ou tout le monde en deux ; les messages tout prêts (« Client en caisse ») se font en un clic aussi. Le PC sonné affiche une popup dans un coin de l'écran et sonne jusqu'à une réponse, et la réponse (« J'arrive ») revient sur votre liste.

Un widget dans la barre des tâches sonne votre favori en un clic. « Ne pas déranger » pendant 30 minutes, une heure ou jusqu'à demain garde les sonneries pour plus tard. La cloche sonne même quand le son de Windows est coupé.

Pas de serveur, pas de compte : les PC se trouvent sur le réseau local, sur n'importe quelle carte réseau ; derrière un routeur, ajoutez-en un une fois par son adresse.

Gratuit et open source (MIT).
```

**Fonctionnalités** (une par champ)

```
Sonner un PC du réseau local en un clic, avec ou sans message
Des messages tout prêts et des réponses en un clic
Sonner tout le monde en deux clics
Une popup dans le coin de votre choix, discrète ou voyante
La cloche sonne jusqu'à une réponse, même son de Windows coupé
Trois cloches et une boîte à musique, ou votre propre fichier WAV ou MP3, à un volume à elle
Ne pas déranger : 30 minutes, une heure, jusqu'à demain ; les sonneries manquées gardées
Un widget dans la barre des tâches : votre favori sonné en un clic
L'historique des sonneries reçues et envoyées, avec « Rappeler »
Des raccourcis clavier pour sonner un PC ou ouvrir LocalBell
Trouve les autres PC tout seul, sur n'importe quel réseau ; par adresse derrière un routeur
Pas de serveur, pas de compte, rien sur internet
Thème clair ou sombre, français et anglais
```

**Description courte**

```
Une sonnette entre les PC de votre boutique ou de votre bureau : sonnez un collègue en un clic, avec ou sans message, et recevez sa réponse.
```

**Captures d'écran** : `.github/hero.fr.png` (1560 × 1000, the README picture with the texts of the French app:
`dotnet run .local\readme\hero.cs -- D fr`).

**Termes de recherche** (un par champ)

```
sonnette
appeler un collègue
interphone
réseau local
messagerie lan
sonner
cloche bureau
```

**Copyright et marques**

```
Copyright (c) 2026 QuentinPerou.
```

**Développé par** : `QuentinPerou`. **Nouveautés de cette version** : vide pour la première soumission.
