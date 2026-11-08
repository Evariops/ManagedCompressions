# Plan d'action — ManagedCompressions

Créé le 8 novembre 2026. Une étape = une PR, parfois quelques-unes. La PR qui termine une étape coche
sa case ici. Les décisions prises en route vont dans le [journal](#journal), en bas.

## Décisions prises

- **Cibles : `net10.0` et `net11.0`, tout de suite.** Le double ciblage n'attend pas la GA de .NET 11 :
  il se construit avec le SDK 11 rc.1 qu'épingle `global.json`. Pas de `net8.0`, dont le support
  s'arrête le 10 novembre 2026.
- **Un sujet = une PR**, des commits réguliers, des PR empilées quand l'une dépend de l'autre. Les
  merges reviennent à Raphaël : la protection de `main` refuse un merge sans revue.
- **Pas d'objectif 1.0.** On reste en 0.x, avec une release mineure par phase livrée. L'API peut encore
  changer, mais chaque changement est voulu et annoncé dans la release.
- **Les mêmes octets que libzstd et lz4, streaming compris.** Chaque nouvelle fonction a pour oracle
  l'API native qui lui correspond (`ZSTD_compressStream2`, `ZSTD_decompressStream`, `LZ4F_*`),
  appelée avec la même suite d'appels.
- **Une API calquée sur `System.IO.Compression` de .NET 11** (`ZstandardEncoder`, `ZstandardDecoder`,
  `ZstandardStream`) : passer de l'une à l'autre revient à changer des noms de types.

## Décisions

| # | Question | Décision | Statut |
|:--|:--|:--|:--|
| D1 | lz4 1.11.0 est sorti et écrit d'autres octets, à tous les niveaux : la PR Renovate #7 (NativeCompressions 1.3.0) échoue sur tous les tests différentiels LZ4. | Épingler lz4 1.10.0 maintenant (0.2), porter 1.11.0 au début de la phase 5. | Acceptée |
| D2 | Les noms des types de streaming. | `ZstdDecoder`, `ZstdEncoder`, `ZstdStream`, `ZstdCompressionOptions`, `ZstdDecompressionOptions`. `ZstdCompressor` et `ZstdDecompressor` restent l'API one-shot. Encore ouvert : un `ZstdDictionary` préparé une fois et partagé entre encodeurs. | Acceptée |
| D3 | 32 bits (win-x86, linux-arm) et WebAssembly : supportés ou exclus ? | Les tester une fois (0.6), puis soit une jambe de CI, soit une exclusion écrite dans le README. | Acceptée |
| D4 | Le brouillon de release v0.2.1 contient une nouvelle API publique (`ZstdExperimentalOptions`, #10). | Le publier en 0.3.0 plutôt qu'en 0.2.1. | À faire par Raphaël : la publication pousse sur nuget.org |

## Suivi des PR

Les cases du plan se cochent quand la PR est mergée ; en attendant, ce tableau dit où en est chaque étape.

| Étape | PR | Base | État |
|:--|:--|:--|:--|
| Plan | #12 | `main` | en revue |
| 0.2 Renovate : lz4 épinglé, #7 fermée | #13 | `main` | en revue ; #6 et #9 à merger par Raphaël |
| 0.3 Oracle de test hors .NET 11 | #14 | `main` | en revue |
| 0.4 `net10.0` + `net11.0` | #15 | #14 | en revue |
| 0.5 CI : arm64, jeux d'instructions, Native AOT | #17 | #15 | en revue |
| 0.6 Big-endian refusé, processeurs documentés (D3) | #18 | `main` | en revue |
| 0.7 `ZstdError` et `LastError` publics | #16 | `main` | en revue |
| 1 Fuzzing guidé par la couverture | #19 | `main` | en revue |
| 2.1 à 2.4, 2.6 `ZstdDecoder` | #20 | #15, avec #16 | en revue |
| 2.5 `ZstdStream` en lecture | #21 | #20 | en revue |
| 0.1 SDK .NET 11 GA | — | — | attend la GA |

## Vue d'ensemble

| Phase | Contenu | Livraison |
|:--|:--|:--|
| 0 | Socle : .NET 11 GA, `net10.0`, un oracle de test hors .NET 11, CI multi-architecture, plateformes, raisons des refus | release 0.x |
| 1 | Fuzzing guidé par la couverture | CI seulement |
| 2 | Décompression Zstd en streaming, `ZstdStream` en lecture | release 0.x |
| 3 | Paramètres de compression Zstd (one-shot) | release 0.x |
| 4 | Compression Zstd en streaming, `ZstdStream` en écriture | release 0.x |
| 5 | lz4 1.11.0, format de frame LZ4, `Lz4Stream` | release 0.x |

L'ordre est 0, 1, 2, 3, 4, 5. La phase 5 ne dépend pas des phases 2 à 4 : elle peut avancer en
parallèle de la phase 4.

## Ce qu'une étape livre avant d'être cochée

- Des tests contre l'oracle natif, sur `net10.0` et `net11.0`.
- Pour tout nouveau chemin de décodage : une cible de fuzz, et des buffers gardés (`GuardedBuffer`).
- Aucune allocation par appel une fois l'instance chaude
  ([AllocationTests.cs](tests/ManagedCompressions.Zstd.Tests/AllocationTests.cs)).
- Si elle touche un chemin chaud : des mesures sur les deux machines (Apple M4 Pro arm64, AMD Ryzen 9
  7950X x64), sous le JIT et en Native AOT.
- La documentation, dans la même PR : README racine, README Zstd, SECURITY.md, tableau « What it
  supports ».

---

## Phase 0 — Socle

### 0.1 Le SDK de .NET 11

Le double ciblage (0.4) se fait maintenant, avec le SDK 11 rc.1. La GA ne change que le SDK épinglé.

- [ ] Passer `global.json` au SDK 11.0.100 GA dès sa sortie, avec `allowPrerelease: false`.
- [ ] Vérifier que l'API Zstandard de la GA est celle de la rc.1, relevée le 8 novembre :
  `ZstandardCompressionOptions.WindowLog2`, `ZstandardDecompressionOptions.MaxWindowLog2`,
  `SetPrefix`, `SetSourceLength`, `TargetBlockSize`. Les tests et les benchs l'utilisent, et les
  phases 2 à 4 la copient.
- [ ] Si [dotnet/runtime#135603](https://github.com/dotnet/runtime/issues/135603) est corrigé dans la
  GA, compiler de nouveau les benchs AOT x64 avec `native`.

Fini quand : la CI passe avec le SDK GA.

### 0.2 Dépendances et versions amont

- [ ] Merger #6 (System.IO.Hashing 10.0.14, qui convient aux deux cibles).
- [ ] Fermer #7. Épingler `NativeCompressions.LZ4` à 1.1.0 (lz4 1.10.0) dans `renovate.json`, et
  séparer ce paquet de `NativeCompressions.Zstandard`, pour que l'un puisse monter sans l'autre (D1).
- [x] Avant de laisser monter `NativeCompressions.Zstandard`, vérifier sa version de zstd
  (`ZSTD_versionNumber`) : la 1.3.0 embarque toujours zstd 1.5.7.
- [ ] Merger #9 (ubuntu 26) avec la nouvelle CI (0.5).

Fini quand : plus aucune PR Renovate n'échoue, et `renovate.json` explique l'épinglage.

### 0.3 Un oracle de test qui ne dépend pas de .NET 11

13 fichiers de tests Zstd utilisent `System.IO.Compression` (`ZstandardEncoder`, `ZstandardDecoder`,
`ZstandardDictionary`), surtout à travers `NativeZstd`
([CorpusCase.cs:113](tests/ManagedCompressions.Zstd.Tests/Support/CorpusCase.cs#L113)). Ces types
n'existent qu'en .NET 11 : en `net10.0`, ces tests ne compilent pas.

- [ ] Les faire passer par l'interop publique de NativeCompressions.Zstandard 1.1.0 (zstd 1.5.7),
  `NativeCompressions.Interop.ZstandardNativeMethods`, à côté de `ZSTD_compress2` et des fonctions de
  dictionnaire. Les fonctions de streaming et de réglage dont on aura besoin jusqu'à la phase 4 y sont
  publiques (vérifié) : `ZSTD_compressStream2`, `ZSTD_decompressStream`, `ZSTD_CCtx_setParameter`,
  `ZSTD_CCtx_setPledgedSrcSize`, `ZSTD_DCtx_setParameter`. Le paquet a des builds net8, net9, net11 et
  netstandard, il se restaure donc en `net10.0` (vérifié). Et le bench vérifie déjà que ses octets
  sont ceux du code managé.
- [ ] Un test garde-fou : `ZSTD_versionNumber() == 10507` et `LZ4_versionNumber() == 11000`. Si
  l'oracle change de version, c'est ce test qui échoue, et pas 500 tests différentiels.
- [ ] Garder quelques tests croisés contre `System.IO.Compression`, sous `#if NET11_0_OR_GREATER`.
- `LibzstdOracle` (décodage one-shot, fuzzing) et `LibzstdMt` (zstdmt) restent sur
  `tools/native-ref`, qui ne dépend pas de la version de .NET.

Fini quand : la suite Zstd compile sans `System.IO.Compression`, hors des tests croisés.

### 0.4 Multi-ciblage `net10.0` + `net11.0`

Constaté le 8 novembre : les deux bibliothèques compilent en `net10.0` sans aucune modification.

- [ ] Bibliothèques et tests en `<TargetFrameworks>net10.0;net11.0</TargetFrameworks>`
  ([Directory.Build.props](Directory.Build.props)) : chaque build de la bibliothèque est testé sur son
  runtime.
- [ ] Les benchs multi-ciblés eux aussi, les candidats `System.IO.Compression` sous
  `#if NET11_0_OR_GREATER`, et BenchmarkDotNet lancé sur les deux runtimes.
- [ ] Dans l'étape « Check the packages » de la CI, vérifier que chaque paquet contient `lib/net10.0`
  et `lib/net11.0`.
- [ ] Mesurer .NET 10 contre .NET 11 sur les deux machines (silesia, records, records + dictionnaire ;
  JIT et AOT). Si .NET 10 perd plus de ~5 %, choisir entre une correction ciblée (`#if`) et une
  mention dans le README.
- [ ] README racine, README Zstd et descriptions des paquets : « .NET 10 et .NET 11 ».

Fini quand : `dotnet test` passe sur les deux cibles, et le README donne les chiffres de .NET 10.

### 0.5 CI : architectures et jeux d'instructions

Aujourd'hui, la CI est un seul job sur ubuntu-24.04 x64, de 6 à 9 minutes. Les chemins `AdvSimd`
d'arm64 n'y sont jamais testés, les replis scalaires non plus, et rien ne garantit qu'un runner x64
ait AVX-512.

Les variables d'environnement, vérifiées le 8 novembre sur le 7950X avec .NET 10.0.14 et 11.0.0-rc.1 :

| Variable | Effet |
|:--|:--|
| `DOTNET_EnableAVX512=0` | coupe AVX-512 |
| `DOTNET_EnableAVX512F=0` | **aucun effet** : c'est l'ancien nom, ignoré |
| `DOTNET_EnableAVX2=0` | coupe AVX2, BMI2 et AVX-512 |
| `DOTNET_EnableHWIntrinsic=0` | coupe tout, `X86Base` compris : chemins scalaires |
| `DOTNET_PreferredVectorBitWidth=256` | `Vector512` n'est plus accéléré, mais `Avx512F.IsSupported` reste vrai |

- [ ] Une matrice {ubuntu x64, ubuntu arm64} × {`net10.0`, `net11.0`}.
- [ ] Des jambes par jeu d'instructions. En x64 : par défaut, `EnableAVX512=0`, `EnableAVX2=0`,
  `EnableHWIntrinsic=0`. En arm64 : par défaut, `EnableHWIntrinsic=0`.
- [ ] Afficher en tête de chaque job les jeux d'instructions détectés, pour savoir ce que le run a
  couvert. Les chemins AVX-512 se testent en local sur le 7950X avant tout merge qui les touche.
- [ ] Mettre en cache `tools/native-ref/out` et les données de test (clé : version de zstd et hash
  des scripts), pour ne pas recompiler libzstd dans chaque jambe.
- [ ] Si la matrice devient trop longue : un sous-ensemble rapide dans les jambes par jeu
  d'instructions sur les PR, et la suite complète chaque nuit.
- [ ] Un smoke test Native AOT : publier une petite application qui fait un aller-retour zstd et
  lz4, pour linux-x64 et linux-arm64, en `net10.0` et `net11.0`. La promesse « compiles with Native
  AOT » est alors vérifiée à chaque PR.
- [ ] Facultatif : macOS (arm64), et Windows, qui demande llvm-mingw pour `tools/native-ref`.

Fini quand : la règle de branche exige la matrice, et chaque run affiche ce qu'il a couvert.

### 0.6 Plateformes

- [ ] Big-endian (s390x, que Red Hat livre) : certains champs du format sont lus dans l'ordre de la
  machine, par exemple l'offset LZ4
  ([Lz4Decoder.cs:134](src/ManagedCompressions.Lz4/Internal/Lz4Decoder.cs#L134)). Recommandation :
  little-endian seulement, refusé explicitement (une `PlatformNotSupportedException` aux points
  d'entrée ; `BitConverter.IsLittleEndian` est une constante pour le JIT, ça ne coûte rien), et dit
  dans le README à la place de « runs wherever .NET runs ».
- [ ] 32 bits et WebAssembly (D3) : faire tourner une fois les golden files et des allers-retours,
  puis décider entre une jambe de CI et une exclusion documentée.

Fini quand : le README dit exactement où les bibliothèques tournent, et c'est testé.

### 0.7 Dire pourquoi une frame est refusée

- [ ] Rendre publics `ZstdDecompressor.LastError` et l'enum `ZstdError` (23 valeurs, internes
  aujourd'hui), avec une documentation par valeur. Le `ZstdDecoder` de la phase 2 reprendra les mêmes
  codes.
- [ ] Facultatif : la même chose pour `Lz4Block.TryDecompress`.

Fini quand : on peut comprendre un `InvalidData` sans débogueur.

---

## Phase 1 — Fuzzing guidé par la couverture

### 1.1 Les cibles

- [ ] Un projet `fuzz/ManagedCompressions.Fuzz`, une cible par argument. SharpFuzz instrumente les
  assemblies, libFuzzer pilote (libfuzzer-dotnet, sous Linux). Vérifier d'abord que SharpFuzz
  fonctionne avec .NET 10 et 11.
- [ ] Les entrées passent par des buffers gardés (le `GuardedBuffer` des tests) : un seul octet lu ou
  écrit hors limites fait planter le processus, et libFuzzer le signale.
- [ ] Les cibles de départ :
  - `zstd-decompress` : avec et sans dictionnaire, et `DecompressPrefix` ;
  - `zstd-differential` : mêmes acceptations, mêmes refus et même contenu que libzstd en one-shot
    (`LibzstdOracle`) ;
  - `zstd-roundtrip` : données et niveau tirés de l'entrée, compression puis décompression ;
  - `zstd-compress-exact` : les octets du code managé sont ceux de libzstd (niveaux 1 à 9 à chaque
    itération, un échantillon au-delà, pour garder la vitesse) ;
  - `lz4-decompress`, `lz4-roundtrip`, `lz4-compress-exact`.
- [ ] Le corpus de départ : les golden files, le corpus decodecorpus, les petites frames et petits
  blocs des tests, le dictionnaire github.
- [ ] Les limites : `-timeout` contre les boucles sans fin, `-rss_limit_mb` contre les allocations
  commandées par l'entrée, qui sont des bugs selon SECURITY.md.

### 1.2 En CI

- [ ] Un workflow `fuzz.yml` : quelques minutes par cible sur chaque PR (le corpus de régression et
  une courte exploration), environ une heure par cible chaque nuit. Le corpus est conservé d'un run à
  l'autre (cache ou artefact). Le workflow passe zizmor en mode pedantic : permissions minimales,
  actions épinglées par SHA.
- [ ] Chaque crash trouvé devient un test de régression, dans la PR qui le corrige.

Fini quand : le fuzzing tourne chaque nuit et n'a rien trouvé depuis une semaine. Les phases 2, 4 et 5
y ajoutent leurs cibles.

---

## Phase 2 — Décompression Zstd en streaming

Pourquoi en premier : les frames sans taille déclarée (zstd sur stdin, `ZSTD_compressStream` sans
taille annoncée, HTTP `Content-Encoding: zstd`, Kafka) ne se décodent proprement qu'en streaming. Le
streaming lève aussi la limite des 2 GiB, et borne la mémoire par la fenêtre plutôt que par le contenu.

### 2.1 API (D2)

- [ ] `ZstdDecoder`, sur le modèle de `ZstandardDecoder`. `Decompress(source, destination, out
  bytesConsumed, out bytesWritten)` progresse partiellement : après `NeedMoreData` ou
  `DestinationTooSmall`, ce qui pouvait être consommé et écrit l'a été. `Reset()`. Un constructeur qui
  prend `ZstdDecompressionOptions { Dictionary, MaxWindowLog }`.
- [ ] `ZstdDecompressor` ne change pas. Son `Decompress` a la même signature mais une autre
  sémantique (rien n'est consommé après `NeedMoreData`) ; lui donner un comportement de streaming
  casserait sans bruit les appelants qui rappellent avec la frame entière.
- [ ] Facultatif : `ZstdDecompressor.TryGetMaxDecompressedLength`, une borne à la
  `ZSTD_decompressBound`, pour décoder en one-shot une frame sans taille. La BCL la fournit.

### 2.2 Le moteur

- [ ] Une machine à états par frame : l'en-tête (18 octets au plus, accumulés s'ils arrivent en
  morceaux), l'en-tête de bloc (3 octets), le corps du bloc (lu en place s'il est entier dans
  `source`, sinon accumulé dans un tampon interne), le checksum, les frames skippables (leur taille
  tient sur 32 bits : compter en `long`), les frames concaténées.
- [ ] La fenêtre : un tampon de la taille de la fenêtre, plus 128 KiB (un bloc), plus la marge des
  copies larges (`DecoderDictionary.Margin`). Il est alloué à la première frame puis réutilisé (il
  grandit, ne rétrécit jamais) : rien n'est alloué une fois le décodeur chaud.
- [ ] `MaxWindowLog`, 27 par défaut comme dans libzstd (`ZSTD_WINDOWLOG_LIMIT_DEFAULT`), vérifié avant
  toute allocation. Pour une frame single-segment, la fenêtre est la taille du contenu, et la limite
  s'y applique aussi.
- [ ] L'historique : quand le tampon reboucle, l'ancien segment devient un historique externe, avec le
  mécanisme qui sert aujourd'hui aux dictionnaires (le paramètre `history` de `DecompressFrame`,
  `HistoryEnd` et `HistoryLength` dans
  [ZstdDecompressor.Execution.cs](src/ManagedCompressions.Zstd/ZstdDecompressor.Execution.cs#L50)).
  Point à étudier : un dictionnaire plus un rebouclage font trois segments. Lire comment
  `ZSTD_decompressContinue` et `ZSTD_checkContinuity` cessent d'utiliser le dictionnaire, et faire
  de même.
- [ ] La sortie : les blocs décodés sont copiés du tampon vers la destination au rythme de la place
  disponible, comme dans l'étape « flush » de libzstd.
- [ ] Le checksum : une instance `XxHash64` par décodeur (`Append`, `GetCurrentHashAsUInt64`,
  `Reset`). Aujourd'hui, tout le code calcule le hash en une fois (`XxHash64.HashToUInt64`).
- [ ] Un chemin rapide : si la frame entière est dans `source` et que la taille qu'elle déclare tient
  dans `destination`, appeler directement `DecompressFrame`, sans copie. Les petites frames (records)
  gardent ainsi la vitesse du one-shot. libzstd prend le même raccourci dans `ZSTD_decompressStream`
  (« single-pass mode »), apparemment avant son contrôle de fenêtre : en reproduire exactement les
  conditions, sinon on n'accepte pas les mêmes frames.
- [ ] Le décodage par paires de blocs (`DecodeBlockPair`) reste coupé dans le chemin streaming au
  début ; à réévaluer avec les mesures.
- [ ] Après un `InvalidData`, le décodeur reste en erreur jusqu'au `Reset()`.

### 2.3 Validation

- [ ] Accepter et refuser ce que `ZSTD_decompressStream` accepte et refuse, et non `ZSTD_decompress` :
  plafond de fenêtre, taille de bloc limitée à min(fenêtre, 128 KiB). Le commentaire de
  [LibzstdOracle.cs](tests/ManagedCompressions.Zstd.Tests/Support/LibzstdOracle.cs) relève déjà ces
  écarts.

### 2.4 Tests

- [ ] Chaque frame du corpus (golden files, decodecorpus, silesia aux 8 niveaux, records github avec
  et sans dictionnaire), décodée avec plusieurs découpages : 1 octet en entrée et en sortie, des
  tailles aléatoires (graine fixe), tout d'un coup. Le contenu doit être celui du one-shot.
- [ ] Des frames écrites par `ZSTD_compressStream2` : sans taille de contenu, avec des `ZSTD_e_flush`
  au milieu, des fenêtres de 2^10 à 2^27, plusieurs frames par flux, des frames skippables
  intercalées, des checksums.
- [ ] Les refus : fenêtre au-delà de `MaxWindowLog`, flux coupé à chaque octet (`NeedMoreData`, jamais
  d'exception), données corrompues. Le verdict doit être celui de `ZSTD_decompressStream` avec le même
  `ZSTD_d_windowLogMax`.
- [ ] Buffers gardés ; test d'allocations (rien par frame une fois chaud) ; une mémoire conforme à ce
  que la documentation annonce.
- [ ] Une cible de fuzz `zstd-stream-decompress` : découpage tiré de l'entrée, comparaison avec
  `ZSTD_decompressStream`.

### 2.5 `ZstdStream` en lecture

- [ ] `ZstdStream(Stream, CompressionMode.Decompress, leaveOpen)` et les surcharges de
  `ZstandardStream` (options, décodeur fourni) : `Read(Span<byte>)`, `ReadAsync(Memory<byte>)`,
  `CopyTo` et `CopyToAsync`, un tampon d'entrée loué à `ArrayPool`, les frames concaténées. Un flux
  qui s'arrête au milieu d'une frame lève une `InvalidDataException`.
- [ ] Le même comportement que `ZstandardStream` (données après la dernière frame, `leaveOpen`,
  `Dispose`), vérifié par des tests croisés en `net11.0`.

### 2.6 Mesures et documentation

- [ ] Bench : le streaming contre le one-shot (code managé), contre `ZSTD_decompressStream`
  (NativeCompressions) et contre `ZstandardStream` (.NET 11). Silesia en une frame sans taille, et les
  records en un seul flux. Objectifs : le streaming à moins de ~10 % du one-shot sur les grosses
  frames (le coût de la copie de sortie) ; au moins aussi rapide que le streaming natif sur x64, et
  devant sur arm64.
- [ ] README : une section Streaming, le tableau « What it supports », la ligne « Not supported »
  corrigée. SECURITY.md : en streaming, la mémoire est bornée par `MaxWindowLog`.

Fini quand : les tests et le fuzzing de nuit sont propres, et les chiffres publiés.

---

## Phase 3 — Paramètres de compression Zstd

- [ ] `ZstdCompressionOptions`, sur le modèle de `ZstandardCompressionOptions` : le niveau,
  `WindowLog`, `AppendChecksum`, `EnableLongDistanceMatching`, `Dictionary`. Plus deux réglages de
  libzstd que la BCL n'a pas : écrire ou non la taille de contenu (`ZSTD_c_contentSizeFlag`), écrire ou
  non l'ID de dictionnaire (`ZSTD_c_dictIDFlag`). Un constructeur `ZstdCompressor(ZstdCompressionOptions)`.
- [ ] `WindowLog` : reproduire `ZSTD_getCParamsFromCCtxParams`. La valeur remplace celle de la table,
  puis passent `ZSTD_adjustCParams_internal` et l'interaction avec le LDM. C'est là que se joue
  l'identité des octets.
- [ ] LDM : rendre public le réglage `LongDistanceMatching`, interne aujourd'hui
  ([ZstdCompressor.cs:102](src/ManagedCompressions.Zstd/ZstdCompressor.cs#L102)) et déjà utilisé par
  les tests.
- [ ] Les deux drapeaux d'en-tête. Sans taille de contenu, la frame ne peut plus être single-segment :
  l'en-tête porte alors un descripteur de fenêtre.
- [ ] Tests : les octets de `ZSTD_compress2` avec `ZSTD_CCtx_setParameter`, pour chaque niveau, chaque
  `windowLog` de 10 à 27, des tailles autour des seuils des tables (16 KiB, 128 KiB, 256 KiB), avec ou
  sans dictionnaire, avec ou sans LDM.
- Hors périmètre : `TargetBlockSize` (`ZSTD_c_targetCBlockSize`, qui écrit des super-blocs : un gros
  chantier à lui seul), la stratégie et les paramètres fins (`chainLog`, `searchLog`…).

Fini quand : ces réglages sont publics, documentés, et donnent les octets de libzstd.

---

## Phase 4 — Compression Zstd en streaming

Une frame écrite en streaming n'est pas la frame one-shot. Les paramètres sont ceux d'une taille
inconnue, l'en-tête n'a pas de taille de contenu, et les blocs sont coupés par le tampon d'entrée et
par les flush (le pré-découpeur de blocs ne voit qu'un tronçon de 128 KiB à la fois). La garantie
devient : **les octets de `ZSTD_compressStream2`, pour la même suite d'appels.**

### 4.1 Écrire d'abord ce que fait libzstd

- [ ] Décrire dans la première PR le comportement exact de libzstd 1.5.7 :
  - `ZSTD_CCtx_init_compressStream2` : les paramètres pour une taille annoncée ou inconnue. Un premier
    appel en `ZSTD_e_end` annonce la taille tout seul, et avec une destination d'au moins
    `ZSTD_compressBound` octets, il prend un raccourci qui écrit la frame one-shot.
  - `ZSTD_compressStream_generic` : le tampon d'entrée (la fenêtre plus un bloc), la compression dès
    qu'un bloc est plein, `ZSTD_e_flush`, `ZSTD_e_end`, le tampon de sortie.
  - `ZSTD_compressContinue_internal`, puis `ZSTD_compress_frameChunk`, tronçon par tronçon.
  - Le rebouclage du tampon (`ZSTD_window_update`, qui passe en extDict),
    `ZSTD_window_enforceMaxDist`, `ZSTD_overflowCorrectIfNeeded`.
  - Les choix automatiques pour une taille inconnue : LDM, row matchfinder, découpeur de blocs.

### 4.2 Implémenter par paliers, chacun identique à libzstd avant le suivant

- [ ] a. `ZstdEncoder`, sur le modèle de `ZstandardEncoder` : `Compress(source, destination, out
  bytesConsumed, out bytesWritten, isFinalBlock)`, `Flush`, `SetSourceLength`, `Reset`,
  `GetMaxCompressedLength(long)`, et les options de la phase 3. Un seul appel final, avec une
  destination assez grande, donne la frame one-shot d'aujourd'hui.
- [ ] b. Une taille annoncée, et un flux plus court que la fenêtre plus un bloc : le découpage par le
  tampon, le pré-découpeur par tronçon, `Flush`.
- [ ] c. Une taille inconnue : les paramètres de la ligne « taille inconnue » des tables, un en-tête
  sans taille et sans single-segment.
- [ ] d. Le rebouclage : l'ancien segment passe en extDict. Les variantes extDict des chercheurs de
  correspondances existent déjà pour les dictionnaires
  ([FastMatchFinder.Dictionary.cs](src/ManagedCompressions.Zstd/Internal/FastMatchFinder.Dictionary.cs),
  [DoubleFastMatchFinder.Dictionary.cs](src/ManagedCompressions.Zstd/Internal/DoubleFastMatchFinder.Dictionary.cs),
  `LazyMatchFinder`, `OptimalMatchFinder`, `LongDistanceMatcher`), et `MatchState` a les champs de
  `ZSTD_window_t` (`Base`, `DictLimit`, `LowLimit`, `NextToUpdate`, `LoadedDictEnd`). Vérifier,
  stratégie par stratégie et row matchfinder compris, que chaque variante que choisit
  `ZSTD_selectBlockCompressor(…, ZSTD_extDict)` existe.
- [ ] e. Les dictionnaires en streaming, avec les modes attach et copy d'aujourd'hui.
- [ ] f. Le LDM en streaming : au niveau 22 avec une taille inconnue, la fenêtre fait 2^27 et libzstd
  active le LDM de lui-même.
- [ ] g. La correction de débordement des index (`ZSTD_reduceIndex`), pour les flux de plus de
  ~3,5 GiB. Elle n'existe pas aujourd'hui, puisque les entrées font moins de 2 GiB.

### 4.3 `ZstdStream` en écriture

- [ ] `Write` et `WriteAsync`, `Flush` et `FlushAsync` (un `ZSTD_e_flush`), `Dispose` et
  `DisposeAsync`, qui écrivent la fin de la frame, `SetSourceLength`.

### 4.4 Tests

- [ ] Les octets de `ZSTD_compressStream2`, pour la même suite d'appels : tailles de morceaux
  (1 octet, aléatoires, 128 KiB pile, tout d'un coup), points de flush, taille annoncée ou non, niveaux
  de -131072 (un échantillon) à 22, flux autour de la taille de la fenêtre et de la fenêtre plus un
  bloc, dictionnaires, checksum, LDM.
- [ ] Des flux de plusieurs GiB, la nuit seulement, pour le rebouclage et la correction des index.
- [ ] Des allers-retours par notre décodeur et par libzstd. Une cible de fuzz
  `zstd-stream-compress-exact` (données, découpage, flush et niveau tirés de l'entrée).

### 4.5 Mesures et documentation

- [ ] Le streaming contre le one-shot, et contre le streaming natif, sur les deux machines. README :
  la garantie d'octets du streaming, dite clairement.
- Hors périmètre : le streaming multithread (zstdmt en streaming). `CompressAsync` couvre le one-shot.

Fini quand : `ZstdEncoder` et `ZstdStream` écrivent les octets de libzstd, et le fuzzing de nuit est
propre.

---

## Phase 5 — LZ4 : version amont et format de frame

### 5.0 lz4 1.11.0 (D1)

- [ ] Lire les différences entre 1.10.0 et 1.11.0 dans `lib/lz4.c`, `lib/lz4hc.c` et
  `lib/lz4frame.c`. Porter les compresseurs pour qu'ils écrivent les octets de 1.11.0 (aujourd'hui,
  tous les niveaux diffèrent), et reprendre les gains du décodeur s'ils s'appliquent.
- [ ] Passer `NativeCompressions.LZ4` en 1.3.0, qui est l'oracle des tests et la référence des benchs,
  et refaire les mesures LZ4 du README.

### 5.1 Le décodeur de frame, en streaming d'emblée

- [ ] `Lz4FrameDecoder`, avec la même API `OperationStatus` que `ZstdDecoder`.
- [ ] L'en-tête : magic `0x184D2204`, octets FLG et BD, taille de contenu, ID de dictionnaire, octet
  de contrôle de l'en-tête (`(XXH32 >> 8) & 0xFF`). Les blocs : taille sur 4 octets, bit « non
  compressé », checksum de bloc ; puis l'EndMark et le checksum du contenu (`XxHash32`). Les frames
  skippables (`0x184D2A50` à `0x184D2A5F`). Le format legacy (`0x184C2102`), en décodage seulement :
  à décider.
- [ ] Les blocs liés : le décodeur LZ4 ne connaît pas d'historique
  (`Lz4Decoder.Decode(source, length, destination, capacity)`). Lui ajouter une variante avec préfixe,
  où un offset remonte jusqu'à 64 KiB avant le bloc, comme dans `LZ4_decompress_safe_usingDict`, sans
  rien perdre des garanties de SECURITY.md.
- [ ] Accepter et refuser ce que `LZ4F_decompress` accepte et refuse.

### 5.2 Le compresseur de frame, identique à lz4frame

- [ ] Les blocs indépendants d'abord : ils réutilisent les compresseurs de bloc actuels. Reproduire
  `LZ4F_optimalBSID`, le choix entre le compresseur rapide et HC selon le niveau
  (`LZ4HC_CLEVEL_MIN`), le bloc stocké tel quel quand la compression ne gagne rien, et les valeurs par
  défaut des préférences. Tout cela se lit dans `lz4frame.c`, pas de mémoire.
- [ ] Les blocs liés : les modes de `LZ4_compress_fast_continue` et de `LZ4_compress_HC_continue`
  (préfixe et extDict, sur 64 KiB), le tampon d'entrée de `LZ4F_compressUpdate`, `autoFlush`,
  `LZ4F_localSaveDict`. L'encodeur rapide ne connaît aucun dictionnaire ; l'encodeur HC a déjà un
  préfixe interne (`_prefixStart`, `_prefixIndex`), une base à étendre.
- [ ] `LZ4F_compressFrame` (one-shot), et `LZ4F_compressBegin`, `Update`, `Flush`, `End` (streaming).

### 5.3 Tests

- [ ] Les octets des fonctions `LZ4F_*`, appelées par l'interop publique
  `NativeCompressions.Interop.LZ4NativeMethods` (vérifié), pour la même suite d'appels : tous les
  niveaux, les 4 tailles de bloc, blocs liés et indépendants, checksums, taille de contenu.
- [ ] Le décodage de tout ce que lz4 écrit. Une cible de fuzz `lz4-frame-decompress`, et une
  comparaison avec `LZ4F_decompress`.

### 5.4 `Lz4Stream`, mesures et documentation

- [ ] `Lz4Stream`, en lecture et en écriture. README et SECURITY.md à jour.
- [ ] Des mesures contre `LZ4Encoder` et `LZ4Decoder` de NativeCompressions, qui travaillent eux aussi
  en format frame : c'est ce que leurs utilisateurs emploient.

Fini quand : les frames LZ4 se lisent et s'écrivent à l'octet près.

---

## Plus tard, hors de ce plan

- L'entraînement de dictionnaires en C# (ZDICT : cover et fastcover).
- Les intégrations : fournisseur de compression ASP.NET Core, décompression dans `HttpClient`
  (`Content-Encoding: zstd`, fenêtre de 8 MiB au plus selon la RFC 9659), gRPC, System.IO.Pipelines.
- Le streaming multithread, `TargetBlockSize`, les paramètres fins, `SetPrefix` (la compression
  différentielle, le `--patch-from` de zstd).

## Journal

- **2026-11-08** — Plan créé. Décisions : `net10.0` et `net11.0`, pas de `net8.0` ; pas d'objectif
  1.0. Constats : les deux bibliothèques compilent déjà en `net10.0` ; lz4 1.11.0 change les octets à
  tous les niveaux (PR #7, CI en échec).
- **2026-11-08** — Plan validé. Le double ciblage se fait tout de suite, sur le SDK 11 rc.1, sans
  attendre la GA. D1 à D3 acceptées. NativeCompressions 1.1.0, 1.2.0 et 1.3.0 embarquent toutes
  zstd 1.5.7 ; seule la 1.3.0 change lz4 (1.11.0) ; aucune n'est compilée avec le multithread de
  zstd, donc `LibzstdMt` reste sur `tools/native-ref`. Les merges restent à Raphaël : la protection
  de `main` a refusé celui de #6.
- **2026-11-08** — Phase 0 en PR (#13 à #18). .NET 10 va à la vitesse de .NET 11 sur le 7950X
  (0,99 en moyenne contre le natif). La CI passe sur arm64 et sans AVX-512, AVX2 ni intrinsics ; le
  smoke test Native AOT donne les mêmes empreintes sur linux-x64 et linux-arm64, et le 32 bits x86
  aussi (vérifié à la main). `DOTNET_EnableAVX512F=0` ne fait plus rien depuis .NET 10 :
  `DOTNET_EnableAVX512=0`. Le job de test complet passe de 7 à 17 minutes avec les deux runtimes.
- **2026-11-08** — Fuzzing (#19). Un premier passage `smoke` a montré trois écarts voulus du décodeur
  LZ4 avec lz4 (règles de fin de bloc non imposées, offset nul refusé, destination vide). libFuzzer a
  trouvé en un million d'entrées une frame au format zstd v0.5 : le libzstd de NativeCompressions a
  les décodeurs legacy, pas ce décodeur (RFC 8878 seule). Les deux sont documentés ; la frame v0.5 est
  la première régression. Ouvert : faut-il aligner la validation LZ4 sur celle de lz4 ?
- **2026-11-08** — `ZstdDecoder` (#20) et `ZstdStream` (#21). Le streaming accepte et refuse comme
  `ZSTD_decompressStream`, raccourci one-shot compris (avant le contrôle de fenêtre, comme libzstd).
  Silesia niveau 3 sur le 7950X : 2 031 Mo/s en one-shot, ~1 800 Mo/s en morceaux de 64 Kio
  (−11 %), contre ~1 680 Mo/s pour le streaming de libzstd. Piste : les paires de blocs dans le
  chemin streaming. Correctif au passage : une copie depuis l'historique ne passe plus par
  `CopyForward`, qui confondait un historique dans le même tableau que la sortie avec la sortie.
