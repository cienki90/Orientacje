# Orientacje

Generator podkładów mapowych **OpenStreetMap** do orientacji na rysunkach budowlanych.
Program czyta plik DXF, dla każdego obiektu pobiera mapę w zasięgu **obiekt + 1500 m**,
przelicza ją do układu rysunku (PL-2000 / PL-1992) i zapisuje jako **JPG** z plikiem
pozycjonującym, gotowy do wstawienia w **GstarCAD 2025** we właściwym miejscu i skali.

**Nie wymaga instalowania niczego** – działa na czystym Windows 10/11 (korzysta z wbudowanego
.NET Framework 4).

## Użycie

1. Pobierz repozytorium (*Code → Download ZIP*) i rozpakuj.
2. Kliknij dwukrotnie **`orientacje.bat`** i wybierz plik DXF
   (albo przeciągnij plik DXF na `orientacje.bat`).

Przy pierwszym uruchomieniu `orientacje.bat` kompiluje `orientacje.cs` do `orientacje.exe`
kompilatorem wbudowanym w Windows (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`),
trwa to kilka sekund. Gotowy `orientacje.exe` można potem uruchamiać bezpośrednio lub
skopiować na inny komputer.

Z wiersza poleceń:

```
orientacje.bat Rysunek2.dxf
orientacje.bat -z 17 -w "!TELE" Rysunek2.dxf
orientacje.bat -h
```

W katalogu `Rysunek2_orientacje\` obok DXF powstaną:

| plik | opis |
|---|---|
| `Rysunek2_orientacja_NN.jpg` | mapa, północ do góry, siatka układu rysunku |
| `Rysunek2_orientacja_NN.jgw` | world file (pozycja i rozmiar piksela w metrach) |
| `Rysunek2_orientacja_NN.prj` | opis układu współrzędnych (dla GIS / nakładek) |
| `Rysunek2_orientacje.lsp` | polecenie **ORIENTACJE** do GstarCAD – wstawia wszystkie obrazy |
| `Rysunek2_orientacje.scr` | skrypt: wczytuje LISP i uruchamia ORIENTACJE |
| `Rysunek2_orientacje.txt` | zestawienie zasięgów |
| `*_kontrola.jpg` | (opcja `--kontrola`) mapa z naniesionymi na czerwono obiektami z DXF |

### Wstawienie w GstarCAD 2025

1. Otwórz rysunek (w tych samych współrzędnych co DXF).
2. `APPLOAD` → wybierz `Rysunek2_orientacje.lsp` → wpisz polecenie `ORIENTACJE`.
   Obrazy trafią na warstwę `ORIENTACJA`, pod rysunek, z lewym dolnym narożnikiem
   w (Xmin, Ymin) i szerokością w metrach zgodną z `.jgw`.
3. Alternatywnie: polecenie `SCRIPT` → `Rysunek2_orientacje.scr`.

Sam GstarCAD nie czyta `.jgw` przy `IMAGEATTACH` (korzystają z nich nakładki, np. Spatial
Manager, i programy GIS), dlatego program generuje też LISP z gotowymi współrzędnymi.

## Najważniejsze opcje

| opcja | domyślnie | opis |
|---|---|---|
| `-b, --bufor` | 1500 | odległość od obiektu [m] |
| `-z, --zoom` | 16 | szczegółowość OSM: 15 ≈ 1:25 000, **16 ≈ 1:10 000** (~1,5 m/px), 17 ≈ 1:5 000 (~0,7 m/px) |
| `-p, --piksel` | natywny | rozmiar piksela wynikowego [m] |
| `-g, --grupuj` | 300 | elementy bliżej niż N m tworzą jeden obraz; `-1` = każdy element osobno |
| `--jeden-obraz` | – | jeden obraz dla całego rysunku |
| `-w, --warstwa` | wszystkie | tylko wskazane warstwy (np. `-w "!TELE"`, dozwolone `*`) |
| `--uklad` | auto | `2000-5..8` lub `1992`; wykrywany ze współrzędnych |
| `--zamien-xy` | – | gdy w rysunku X = północ, Y = wschód |
| `--url` | tile.openstreetmap.org | inny serwer kafli `{z}/{x}/{y}` |
| `--kontrola` | – | dodatkowe obrazy z naniesionymi obiektami do sprawdzenia |
| `--tylko-lista` | – | tylko pokaż obiekty i zasięgi (bez pobierania) |

Pełna lista: `orientacje.bat -h`. Obsługiwane encje DXF (ASCII, model): LWPOLYLINE, POLYLINE,
LINE, ARC, CIRCLE, POINT, INSERT, SPLINE, ELLIPSE.

## Jak to działa

1. Odczyt DXF i grupowanie elementów w obiekty.
2. Zasięg = prostokąt obiektu + bufor; wykrycie układu (np. X = 7 5xx xxx → PL-2000 strefa 7).
3. Pobranie kafli OSM (cache w `cache_kafli\` obok programu – drugi raz nie są pobierane).
4. Przeliczenie mozaiki Web Mercator → PL-2000/1992 (Gauss-Krüger, GRS80, dokładność
   przeliczenia < 1 mm) – mapa jest zgodna z siatką rysunku, bez obrotu.
5. Zapis JPG + JGW + PRJ oraz LISP/SCR.

Przykład dla `Rysunek2.dxf` (warstwa `!TELE`, PL-2000 strefa 7): 41 elementów → 8 obrazów
o boku ok. 3–6 km, ok. 0,4–1,3 MB każdy.

Workflow `.github/workflows/windows.yml` kompiluje program na Windows tym samym
`orientacje.bat` i testuje go na `Rysunek2.dxf`; gotowy `orientacje.exe` jest w artefaktach
przebiegu.

## Licencja danych i zasady serwera OSM

Mapy © [OpenStreetMap contributors](https://www.openstreetmap.org/copyright), licencja ODbL –
na obrazie umieszczany jest wymagany podpis (wyłączenie: `--bez-podpisu`, wtedy podpis
trzeba dać na rysunku). Serwer `tile.openstreetmap.org` jest przeznaczony do umiarkowanego
użytku ([Tile Usage Policy](https://operations.osmfoundation.org/policies/tiles/)):
program pobiera kafle po kolei, z cache i limitem `--max-kafli`. Przy dużej liczbie
rysunków użyj innego serwera kafli (`--url`).
