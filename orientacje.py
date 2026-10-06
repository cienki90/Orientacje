#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Orientacje - generator podkładów mapowych OpenStreetMap do rysunków budowlanych.

Program czyta plik DXF (współrzędne w układzie PL-2000 lub PL-1992), wyznacza
obiekty (grupy elementów), dla każdego obiektu pobiera mapę OSM w zasięgu
obiekt + bufor (domyślnie 1500 m), przelicza ją do układu rysunku i zapisuje:

  * <nazwa>.jpg  - obraz mapy (północ do góry, siatka układu PL-2000/1992),
  * <nazwa>.jgw  - plik pozycjonujący (world file),
  * <plik>_orientacje.lsp - polecenie ORIENTACJE dla GstarCAD (wstawia wszystkie
                            obrazy we właściwym miejscu i skali),
  * <plik>_orientacje.scr - to samo w postaci skryptu (alternatywa),
  * <plik>_orientacje.txt - zestawienie zasięgów.

Wymagania: Python 3.8+, Pillow  (pip install pillow)

Przykład:
  python orientacje.py Rysunek2.dxf
  python orientacje.py Rysunek2.dxf --bufor 1500 --zoom 16 --warstwa "!TELE"
"""

from __future__ import annotations

import argparse
import fnmatch
import math
import os
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from io import BytesIO
from typing import Dict, List, Optional, Sequence, Tuple

try:
    from PIL import Image, ImageDraw, ImageFont
except ImportError:  # pragma: no cover
    Image = None  # komunikat wyświetlany w main()

__version__ = "1.0"

DOMYSLNY_URL = "https://tile.openstreetmap.org/{z}/{x}/{y}.png"
USER_AGENT = (
    f"Orientacje/{__version__} (generator orientacji do rysunkow budowlanych; "
    "+https://github.com/cienki90/Orientacje)"
)
ROZMIAR_KAFLA = 256

# ---------------------------------------------------------------------------
#  Odwzorowanie Gaussa-Krügera (szeregi Krügera, elipsoida GRS80)
# ---------------------------------------------------------------------------

_A = 6378137.0
_F = 1.0 / 298.257222101  # GRS80


@dataclass(frozen=True)
class Uklad:
    nazwa: str
    epsg: int
    lon0: float  # południk osiowy [deg]
    k0: float
    e0: float  # false easting
    n0: float  # false northing


def uklad_2000(strefa: int) -> Uklad:
    if strefa not in (5, 6, 7, 8):
        raise ValueError("Strefa PL-2000 musi być 5, 6, 7 lub 8")
    return Uklad(f"PL-2000 strefa {strefa}", 2171 + strefa, 3.0 * strefa,
                 0.999923, strefa * 1_000_000 + 500_000.0, 0.0)


UKLAD_1992 = Uklad("PL-1992", 2180, 19.0, 0.9993, 500_000.0, -5_300_000.0)


class GaussKruger:
    """Odwzorowanie poprzeczne Merkatora - szeregi Krügera do n^4 (dokładność << 1 mm)."""

    def __init__(self, uklad: Uklad):
        self.u = uklad
        n = _F / (2.0 - _F)
        self.n = n
        self.A = _A / (1.0 + n) * (1.0 + n ** 2 / 4.0 + n ** 4 / 64.0)
        self.alfa = (
            n / 2 - 2 / 3 * n ** 2 + 5 / 16 * n ** 3 + 41 / 180 * n ** 4,
            13 / 48 * n ** 2 - 3 / 5 * n ** 3 + 557 / 1440 * n ** 4,
            61 / 240 * n ** 3 - 103 / 140 * n ** 4,
            49561 / 161280 * n ** 4,
        )
        self.beta = (
            n / 2 - 2 / 3 * n ** 2 + 37 / 96 * n ** 3 - 1 / 360 * n ** 4,
            1 / 48 * n ** 2 + 1 / 15 * n ** 3 - 437 / 1440 * n ** 4,
            17 / 480 * n ** 3 - 37 / 840 * n ** 4,
            4397 / 161280 * n ** 4,
        )
        self.delta = (
            2 * n - 2 / 3 * n ** 2 - 2 * n ** 3 + 116 / 45 * n ** 4,
            7 / 3 * n ** 2 - 8 / 5 * n ** 3 - 227 / 45 * n ** 4,
            56 / 15 * n ** 3 - 136 / 35 * n ** 4,
            4279 / 630 * n ** 4,
        )
        self._c = 2.0 * math.sqrt(n) / (1.0 + n)

    def do_geo(self, e: float, nn: float) -> Tuple[float, float]:
        """(easting, northing) -> (szerokość, długość) w stopniach."""
        u = self.u
        kA = u.k0 * self.A
        xi = (nn - u.n0) / kA
        eta = (e - u.e0) / kA
        xi1, eta1 = xi, eta
        for j, b in enumerate(self.beta, start=1):
            xi1 -= b * math.sin(2 * j * xi) * math.cosh(2 * j * eta)
            eta1 -= b * math.cos(2 * j * xi) * math.sinh(2 * j * eta)
        chi = math.asin(math.sin(xi1) / math.cosh(eta1))
        phi = chi
        for j, d in enumerate(self.delta, start=1):
            phi += d * math.sin(2 * j * chi)
        lam = math.atan2(math.sinh(eta1), math.cos(xi1))
        return math.degrees(phi), u.lon0 + math.degrees(lam)

    def z_geo(self, lat: float, lon: float) -> Tuple[float, float]:
        """(szerokość, długość) w stopniach -> (easting, northing)."""
        u = self.u
        phi = math.radians(lat)
        lam = math.radians(lon - u.lon0)
        s = math.sin(phi)
        t = math.sinh(math.atanh(s) - self._c * math.atanh(self._c * s))
        xi1 = math.atan2(t, math.cos(lam))
        eta1 = math.atanh(math.sin(lam) / math.sqrt(1.0 + t * t))
        xi, eta = xi1, eta1
        for j, a in enumerate(self.alfa, start=1):
            xi += a * math.sin(2 * j * xi1) * math.cosh(2 * j * eta1)
            eta += a * math.cos(2 * j * xi1) * math.sinh(2 * j * eta1)
        kA = u.k0 * self.A
        return u.e0 + kA * eta, u.n0 + kA * xi


# ---------------------------------------------------------------------------
#  Web Mercator (kafle OSM)
# ---------------------------------------------------------------------------

def merkator_px(lat: float, lon: float, zoom: int) -> Tuple[float, float]:
    """Globalne współrzędne pikselowe kafli OSM dla danego poziomu zoom."""
    skala = ROZMIAR_KAFLA * (2 ** zoom)
    x = (lon + 180.0) / 360.0 * skala
    phi = math.radians(lat)
    y = (1.0 - math.log(math.tan(phi) + 1.0 / math.cos(phi)) / math.pi) / 2.0 * skala
    return x, y


def rozdzielczosc_osm(lat: float, zoom: int) -> float:
    """Rozmiar piksela kafla OSM w terenie [m]."""
    return 2 * math.pi * 6378137.0 * math.cos(math.radians(lat)) / (ROZMIAR_KAFLA * 2 ** zoom)


# ---------------------------------------------------------------------------
#  Odczyt DXF (ASCII) - bez zewnętrznych bibliotek
# ---------------------------------------------------------------------------

@dataclass
class Obiekt:
    typ: str
    warstwa: str
    uchwyt: str
    xmin: float = math.inf
    ymin: float = math.inf
    xmax: float = -math.inf
    ymax: float = -math.inf

    def dodaj(self, x: float, y: float, r: float = 0.0) -> None:
        self.xmin = min(self.xmin, x - r)
        self.ymin = min(self.ymin, y - r)
        self.xmax = max(self.xmax, x + r)
        self.ymax = max(self.ymax, y + r)

    @property
    def poprawny(self) -> bool:
        return self.xmin <= self.xmax and self.ymin <= self.ymax


TYPY_DOMYSLNE = ("LWPOLYLINE", "POLYLINE", "LINE", "ARC", "CIRCLE", "POINT",
                 "INSERT", "SPLINE", "ELLIPSE")


def _pary_dxf(sciezka: str):
    with open(sciezka, "rb") as f:
        poczatek = f.read(22)
    if poczatek.startswith(b"AutoCAD Binary DXF"):
        raise ValueError("Binarny DXF nie jest obsługiwany - zapisz rysunek jako DXF ASCII.")
    with open(sciezka, "r", encoding="latin-1", newline=None) as f:
        while True:
            kod = f.readline()
            if not kod:
                return
            wartosc = f.readline()
            kod = kod.strip()
            if not kod:
                continue
            try:
                yield int(kod), wartosc.rstrip("\r\n")
            except ValueError:
                raise ValueError(f"Niepoprawna struktura DXF (kod grupy: {kod!r})") from None


def wczytaj_dxf(sciezka: str, typy: Sequence[str] = TYPY_DOMYSLNE,
                warstwy: Optional[Sequence[str]] = None,
                zamien_xy: bool = False) -> List[Obiekt]:
    """Zwraca listę obiektów (prostokąty ograniczające) z sekcji ENTITIES (model)."""
    typy_set = {t.upper() for t in typy}
    obiekty: List[Obiekt] = []
    sekcja = None
    poprzedni: Tuple[int, str] = (-1, "")

    akt: Optional[dict] = None       # bieżąca encja (surowe dane)
    polilinia: Optional[dict] = None  # stara POLYLINE czekająca na VERTEX/SEQEND

    def zakoncz(enc: Optional[dict]) -> None:
        nonlocal polilinia
        if enc is None:
            return
        typ = enc["typ"]
        if typ == "VERTEX":
            if polilinia is not None:
                polilinia["pkt"].extend(enc["pkt"][:1])
            return
        if typ == "SEQEND":
            if polilinia is not None:
                _zapisz(polilinia)
            polilinia = None
            return
        if typ == "POLYLINE":
            polilinia = enc
            return
        _zapisz(enc)

    def _zapisz(enc: dict) -> None:
        if enc["typ"] not in typy_set or enc.get("paper"):
            return
        if warstwy and not any(fnmatch.fnmatchcase(enc["warstwa"].upper(), w.upper())
                               for w in warstwy):
            return
        ob = Obiekt(enc["typ"], enc["warstwa"], enc["uchwyt"])
        lustro = enc.get("ext_z", 1.0) < 0  # OCS z osią Z w dół -> odbicie X
        r = enc.get("r", 0.0)
        pkt = enc["pkt"]
        if enc["typ"] == "ELLIPSE" and pkt:
            r = math.hypot(*enc.get("os", (0.0, 0.0)))
            pkt = pkt[:1]
        elif enc["typ"] in ("CIRCLE", "ARC", "POINT", "INSERT"):
            pkt = pkt[:1]
        elif enc["typ"] == "LINE":
            pkt = pkt[:1] + enc.get("pkt2", [])[:1]
        for x, y in pkt:
            if lustro and enc["typ"] in ("LWPOLYLINE", "CIRCLE", "ARC"):
                x = -x
            if zamien_xy:
                x, y = y, x
            ob.dodaj(x, y, r)
        if ob.poprawny:
            obiekty.append(ob)

    ox: Optional[float] = None
    for kod, wart in _pary_dxf(sciezka):
        if kod == 0:
            if sekcja == "ENTITIES":
                zakoncz(akt)
            akt = None
            if wart == "ENDSEC":
                sekcja = None
            elif sekcja == "ENTITIES":
                akt = {"typ": wart.strip().upper(), "warstwa": "0", "uchwyt": "",
                       "pkt": [], "pkt2": []}
                ox = None
        elif kod == 2 and poprzedni == (0, "SECTION"):
            sekcja = wart.strip().upper()
        elif akt is not None:
            if kod == 8:
                akt["warstwa"] = wart.strip()
            elif kod == 5:
                akt["uchwyt"] = wart.strip()
            elif kod == 67:
                akt["paper"] = wart.strip() == "1"
            elif kod == 10:
                ox = float(wart)
            elif kod == 20 and ox is not None:
                akt["pkt"].append((ox, float(wart)))
                ox = None
            elif kod == 11:
                akt["_x11"] = float(wart)
            elif kod == 21 and "_x11" in akt:
                if akt["typ"] == "ELLIPSE":
                    akt["os"] = (akt.pop("_x11"), float(wart))
                else:
                    akt["pkt2"].append((akt.pop("_x11"), float(wart)))
            elif kod == 40 and akt["typ"] in ("CIRCLE", "ARC"):
                akt["r"] = float(wart)
            elif kod == 230:
                akt["ext_z"] = float(wart)
        poprzedni = (kod, wart.strip())
    if polilinia is not None:
        _zapisz(polilinia)
    return obiekty


# ---------------------------------------------------------------------------
#  Grupowanie obiektów
# ---------------------------------------------------------------------------

@dataclass
class Grupa:
    obiekty: List[Obiekt] = field(default_factory=list)

    @property
    def zasieg(self) -> Tuple[float, float, float, float]:
        return (min(o.xmin for o in self.obiekty), min(o.ymin for o in self.obiekty),
                max(o.xmax for o in self.obiekty), max(o.ymax for o in self.obiekty))


def _odl_prostokatow(a: Obiekt, b: Obiekt) -> float:
    dx = max(0.0, b.xmin - a.xmax, a.xmin - b.xmax)
    dy = max(0.0, b.ymin - a.ymax, a.ymin - b.ymax)
    return math.hypot(dx, dy)


def grupuj(obiekty: List[Obiekt], odstep: float) -> List[Grupa]:
    """Łączy obiekty, których prostokąty są bliżej niż `odstep` (odstep<0 -> każdy osobno)."""
    if odstep < 0:
        return [Grupa([o]) for o in obiekty]
    rodzic = list(range(len(obiekty)))

    def znajdz(i: int) -> int:
        while rodzic[i] != i:
            rodzic[i] = rodzic[rodzic[i]]
            i = rodzic[i]
        return i

    kolejnosc = sorted(range(len(obiekty)), key=lambda i: obiekty[i].xmin)
    for p, i in enumerate(kolejnosc):
        a = obiekty[i]
        for j in kolejnosc[p + 1:]:
            b = obiekty[j]
            if b.xmin > a.xmax + odstep:
                break
            if _odl_prostokatow(a, b) <= odstep:
                ri, rj = znajdz(i), znajdz(j)
                if ri != rj:
                    rodzic[rj] = ri
    grupy: Dict[int, Grupa] = {}
    for i, o in enumerate(obiekty):
        grupy.setdefault(znajdz(i), Grupa()).obiekty.append(o)
    # porządek: z północy na południe, z zachodu na wschód
    return sorted(grupy.values(), key=lambda g: (-round(g.zasieg[3], -2), g.zasieg[0]))


def wykryj_uklad(obiekty: List[Obiekt]) -> Uklad:
    x = sorted((o.xmin + o.xmax) / 2 for o in obiekty)[len(obiekty) // 2]
    y = sorted((o.ymin + o.ymax) / 2 for o in obiekty)[len(obiekty) // 2]
    strefa = int(x // 1_000_000)
    if strefa in (5, 6, 7, 8) and 4_000_000 < y < 7_000_000:
        return uklad_2000(strefa)
    if 100_000 < x < 900_000 and 100_000 < y < 900_000:
        return UKLAD_1992
    raise ValueError(
        f"Nie rozpoznano układu współrzędnych (X={x:.0f}, Y={y:.0f}). "
        "Użyj opcji --uklad oraz ewentualnie --zamien-xy.")


# ---------------------------------------------------------------------------
#  Pobieranie kafli
# ---------------------------------------------------------------------------

class Kafle:
    def __init__(self, url: str, zoom: int, cache: str, opoznienie: float, user_agent: str):
        self.url = url
        self.zoom = zoom
        self.cache = cache
        self.opoznienie = opoznienie
        self.user_agent = user_agent
        self.pobrane = 0
        self.z_cache = 0
        self.bledy = 0
        self._ostatni = 0.0

    def _plik(self, x: int, y: int) -> str:
        return os.path.join(self.cache, str(self.zoom), str(x), f"{y}.img")

    def pobierz(self, x: int, y: int) -> Optional["Image.Image"]:
        n = 2 ** self.zoom
        if not (0 <= y < n):
            return None
        x %= n
        plik = self._plik(x, y)
        if os.path.isfile(plik) and os.path.getsize(plik) > 0:
            try:
                im = Image.open(plik)
                im.load()
                self.z_cache += 1
                return im
            except OSError:
                pass  # uszkodzony plik w cache - pobierz ponownie
        url = self.url.format(z=self.zoom, x=x, y=y, s="abc"[(x + y) % 3])
        req = urllib.request.Request(url, headers={"User-Agent": self.user_agent})
        for proba in range(4):
            czekaj = self.opoznienie - (time.monotonic() - self._ostatni)
            if czekaj > 0:
                time.sleep(czekaj)
            self._ostatni = time.monotonic()
            try:
                with urllib.request.urlopen(req, timeout=30) as odp:
                    dane = odp.read()
                im = Image.open(BytesIO(dane))
                im.load()
                os.makedirs(os.path.dirname(plik), exist_ok=True)
                with open(plik, "wb") as f:
                    f.write(dane)
                self.pobrane += 1
                return im
            except urllib.error.HTTPError as e:
                if e.code in (403, 401):
                    raise RuntimeError(
                        f"Serwer kafli odmówił dostępu (HTTP {e.code}) dla {url}. "
                        "Sprawdź zasady korzystania z serwera lub podaj inny --url.") from e
                if e.code == 404:
                    break
                blad = e
            except (urllib.error.URLError, OSError) as e:
                blad = e
            time.sleep(2 ** proba)
        else:
            print(f"   ! nie pobrano kafla {self.zoom}/{x}/{y}: {blad}")
        self.bledy += 1
        return None

    def mozaika(self, tx0: int, ty0: int, tx1: int, ty1: int) -> "Image.Image":
        szer = (tx1 - tx0 + 1) * ROZMIAR_KAFLA
        wys = (ty1 - ty0 + 1) * ROZMIAR_KAFLA
        obraz = Image.new("RGB", (szer, wys), (242, 239, 233))
        razem = (tx1 - tx0 + 1) * (ty1 - ty0 + 1)
        nr = 0
        for ty in range(ty0, ty1 + 1):
            for tx in range(tx0, tx1 + 1):
                nr += 1
                kafel = self.pobierz(tx, ty)
                if kafel is not None:
                    obraz.paste(kafel.convert("RGB"),
                                ((tx - tx0) * ROZMIAR_KAFLA, (ty - ty0) * ROZMIAR_KAFLA))
                if nr % 25 == 0 or nr == razem:
                    print(f"\r   kafle: {nr}/{razem}", end="", flush=True)
        print()
        return obraz


# ---------------------------------------------------------------------------
#  Tworzenie obrazu w układzie rysunku
# ---------------------------------------------------------------------------

@dataclass
class Wynik:
    nazwa: str
    jpg: str
    xmin: float
    ymin: float
    xmax: float
    ymax: float
    piksel: float
    szer_px: int
    wys_px: int
    liczba_obiektow: int


def _stala(nazwa_nowa: str, nazwa_stara: str, grupa: str):
    grp = getattr(Image, grupa, None)
    if grp is not None and hasattr(grp, nazwa_nowa):
        return getattr(grp, nazwa_nowa)
    return getattr(Image, nazwa_stara)


def _czcionka(rozmiar: int):
    for nazwa in ("arial.ttf", "DejaVuSans.ttf", "LiberationSans-Regular.ttf"):
        try:
            return ImageFont.truetype(nazwa, rozmiar)
        except OSError:
            pass
    try:
        return ImageFont.load_default(size=rozmiar)
    except TypeError:
        return ImageFont.load_default()


def podpis_osm(obraz: "Image.Image", tekst: str) -> None:
    rozmiar = max(14, round(min(obraz.size) / 110))
    rys = ImageDraw.Draw(obraz)
    font = _czcionka(rozmiar)
    l, t, r, b = rys.textbbox((0, 0), tekst, font=font)
    m = rozmiar // 3
    x = obraz.width - (r - l) - 3 * m
    y = obraz.height - (b - t) - 3 * m
    rys.rectangle((x - m, y - m, obraz.width, obraz.height), fill=(255, 255, 255))
    rys.text((x - l, y - t), tekst, fill=(40, 40, 40), font=font)


def generuj_obraz(nazwa: str, zasieg: Tuple[float, float, float, float], gk: GaussKruger,
                  kafle: Kafle, piksel: Optional[float], katalog: str, jakosc: int,
                  podpis: Optional[str], liczba_obiektow: int, max_px: int,
                  max_kafli: int) -> Wynik:
    xmin, ymin, xmax, ymax = zasieg
    zoom = kafle.zoom
    lat_c, _ = gk.do_geo((xmin + xmax) / 2, (ymin + ymax) / 2)
    natywny = rozdzielczosc_osm(lat_c, zoom)
    if not piksel:
        piksel = max(0.05, round(natywny, 2))

    # wyrównanie zasięgu do siatki pikseli
    xmin = math.floor(xmin / piksel) * piksel
    ymin = math.floor(ymin / piksel) * piksel
    szer = int(math.ceil((xmax - xmin) / piksel))
    wys = int(math.ceil((ymax - ymin) / piksel))
    if szer * wys > max_px:
        raise RuntimeError(
            f"Obraz {nazwa} miałby {szer}x{wys} px ({szer * wys / 1e6:.0f} Mpx) - przekroczony "
            f"limit --max-mpx. Zwiększ --piksel, zmniejsz --zoom lub zwiększ limit.")
    xmax = xmin + szer * piksel
    ymax = ymin + wys * piksel

    # siatka (mesh) węzłów: piksel wyjściowy -> piksel globalny Web Mercator
    krok = 128
    kol = list(range(0, szer, krok)) + [szer]
    wier = list(range(0, wys, krok)) + [wys]
    wezly: Dict[Tuple[int, int], Tuple[float, float]] = {}
    for py in wier:
        for px in kol:
            lat, lon = gk.do_geo(xmin + px * piksel, ymax - py * piksel)
            wezly[(px, py)] = merkator_px(lat, lon, zoom)
    mx = [p[0] for p in wezly.values()]
    my = [p[1] for p in wezly.values()]
    tx0 = int(math.floor((min(mx) - 2) / ROZMIAR_KAFLA))
    ty0 = int(math.floor((min(my) - 2) / ROZMIAR_KAFLA))
    tx1 = int(math.floor((max(mx) + 2) / ROZMIAR_KAFLA))
    ty1 = int(math.floor((max(my) + 2) / ROZMIAR_KAFLA))
    liczba_kafli = (tx1 - tx0 + 1) * (ty1 - ty0 + 1)
    if liczba_kafli > max_kafli:
        raise RuntimeError(
            f"Obraz {nazwa} wymaga {liczba_kafli} kafli (limit --max-kafli {max_kafli}). "
            "Zmniejsz --zoom lub zwiększ limit (szanuj zasady serwera OSM).")
    print(f"   zasięg {xmax - xmin:.0f} x {ymax - ymin:.0f} m, obraz {szer} x {wys} px "
          f"(piksel {piksel:g} m, OSM zoom {zoom} ≈ {natywny:.2f} m/px), "
          f"kafli: {liczba_kafli}")

    mozaika = kafle.mozaika(tx0, ty0, tx1, ty1)
    ox, oy = tx0 * ROZMIAR_KAFLA, ty0 * ROZMIAR_KAFLA
    red = int(piksel / natywny)  # zmniejszenie mozaiki przy dużym pikselu (antyaliasing)
    if red >= 2:
        mozaika = mozaika.reduce(red)
    else:
        red = 1

    def zrodlo(px: int, py: int) -> Tuple[float, float]:
        x, y = wezly[(px, py)]
        return (x - ox) / red, (y - oy) / red

    siatka = []
    for iy in range(len(wier) - 1):
        for ix in range(len(kol) - 1):
            x0, x1, y0, y1 = kol[ix], kol[ix + 1], wier[iy], wier[iy + 1]
            quad = (*zrodlo(x0, y0), *zrodlo(x0, y1), *zrodlo(x1, y1), *zrodlo(x1, y0))
            siatka.append(((x0, y0, x1, y1), quad))
    mesh = _stala("MESH", "MESH", "Transform")
    bicubic = _stala("BICUBIC", "BICUBIC", "Resampling")
    obraz = mozaika.transform((szer, wys), mesh, siatka, resample=bicubic)
    del mozaika

    if podpis:
        podpis_osm(obraz, podpis)

    jpg = os.path.join(katalog, nazwa + ".jpg")
    obraz.save(jpg, "JPEG", quality=jakosc, optimize=True, subsampling=0)
    with open(os.path.join(katalog, nazwa + ".jgw"), "w", encoding="ascii") as f:
        f.write(f"{piksel:.10f}\n0.0000000000\n0.0000000000\n{-piksel:.10f}\n"
                f"{xmin + piksel / 2:.6f}\n{ymax - piksel / 2:.6f}\n")
    with open(os.path.join(katalog, nazwa + ".prj"), "w", encoding="ascii") as f:
        f.write(prj_wkt(gk.u))
    return Wynik(nazwa, jpg, xmin, ymin, xmax, ymax, piksel, szer, wys, liczba_obiektow)


def prj_wkt(u: Uklad) -> str:
    return (
        f'PROJCS["{u.nazwa} (EPSG:{u.epsg})",GEOGCS["ETRF2000-PL",DATUM["ETRF2000_Poland",'
        'SPHEROID["GRS 1980",6378137,298.257222101]],PRIMEM["Greenwich",0],'
        'UNIT["degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],'
        f'PARAMETER["latitude_of_origin",0],PARAMETER["central_meridian",{u.lon0:g}],'
        f'PARAMETER["scale_factor",{u.k0}],PARAMETER["false_easting",{u.e0:.0f}],'
        f'PARAMETER["false_northing",{u.n0:.0f}],UNIT["metre",1]]'
    )


# ---------------------------------------------------------------------------
#  Pliki dla GstarCAD
# ---------------------------------------------------------------------------

def _sciezka_cad(p: str) -> str:
    return os.path.abspath(p).replace("\\", "/")


def zapisz_lisp(sciezka: str, wyniki: List[Wynik], warstwa: str) -> None:
    def s(t: str) -> str:
        return '"' + t.replace("\\", "/").replace('"', '\\"') + '"'

    linie = [
        ";;; Orientacje - wstawianie podkładów OSM (wygenerowano automatycznie)",
        ";;; GstarCAD: APPLOAD -> wczytaj ten plik -> polecenie ORIENTACJE",
        "(vl-load-com)",
        "",
        "(defun orient:plik (sciezka / f)",
        "  (cond ((findfile sciezka))",
        "        ((setq f (findfile (strcat (vl-filename-base sciezka) \".jpg\"))) f)",
        "        (orient:katalog-lsp",
        "         (findfile (strcat orient:katalog-lsp (vl-filename-base sciezka) \".jpg\")))))",
        "",
        "(defun orient:warstwa (nazwa / dok)",
        "  (setq dok (vla-get-ActiveDocument (vlax-get-acad-object)))",
        "  (if (not (tblsearch \"LAYER\" nazwa))",
        "    (vl-catch-all-apply 'vla-Add (list (vla-get-Layers dok) nazwa))))",
        "",
        "(defun orient:wstaw (sciezka x y szer / plik ms pt obj w ent)",
        "  (setq plik (orient:plik sciezka))",
        "  (if (not plik)",
        "    (princ (strcat \"\\n! Nie znaleziono pliku: \" sciezka))",
        "    (progn",
        "      (setq ms (vla-get-ModelSpace (vla-get-ActiveDocument (vlax-get-acad-object)))",
        "            pt (vlax-3d-point (list x y 0.0)))",
        "      (setq obj (vl-catch-all-apply 'vla-AddRaster (list ms plik pt 1.0 0.0)))",
        "      (if (vl-catch-all-error-p obj)",
        "        (progn ; zapasowo: polecenie -IMAGEATTACH",
        "          (setq ent (entlast))",
        "          (command \"_.-IMAGEATTACH\" plik (list x y 0.0) 1.0 0.0)",
        "          (setq obj (if (not (equal ent (entlast)))",
        "                      (vlax-ename->vla-object (entlast)))))",
        "      )",
        "      (if obj",
        "        (progn",
        "          (setq w (vla-get-ImageWidth obj))",
        "          (if (and w (> w 0.0) (not (equal w szer 1e-6)))",
        "            (vla-ScaleEntity obj pt (/ szer w)))",
        "          (vl-catch-all-apply 'vla-put-Layer (list obj orient:nazwa-warstwy))",
        "          (vl-catch-all-apply",
        "            '(lambda () (command \"_.DRAWORDER\" (vlax-vla-object->ename obj) \"\" \"_B\")))",
        "          (princ (strcat \"\\nWstawiono: \" plik)))",
        "        (princ (strcat \"\\n! Nie udało się wstawić: \" plik))))))",
        "",
        "(setq orient:katalog-lsp",
        "  (if (findfile \"" + os.path.basename(sciezka) + "\")",
        "    (strcat (vl-filename-directory (findfile \"" + os.path.basename(sciezka)
        + "\")) \"/\")))",
        "(setq orient:nazwa-warstwy " + s(warstwa) + ")",
        "",
        "(defun c:ORIENTACJE ( / cmd)",
        "  (setq cmd (getvar \"CMDECHO\"))",
        "  (setvar \"CMDECHO\" 0)",
        "  (orient:warstwa orient:nazwa-warstwy)",
    ]
    for w in wyniki:
        linie.append(f"  (orient:wstaw {s(_sciezka_cad(w.jpg))} {w.xmin:.4f} {w.ymin:.4f} "
                     f"{w.xmax - w.xmin:.4f})")
    linie += [
        "  (setvar \"CMDECHO\" cmd)",
        "  (command \"_.ZOOM\" \"_E\")",
        "  (princ \"\\nGotowe. Mapy: (c) OpenStreetMap contributors (ODbL).\")",
        "  (princ))",
        "",
        "(princ \"\\nWpisz ORIENTACJE aby wstawić podkłady.\")",
        "(princ)",
    ]
    with open(sciezka, "w", encoding="cp1250", errors="replace", newline="\r\n") as f:
        f.write("\n".join(linie) + "\n")


def zapisz_scr(sciezka: str, wyniki: List[Wynik]) -> None:
    """Skrypt dla polecenia SCRIPT. Obraz bez DPI ma bazową szerokość 1 jednostki,
    więc współczynnik skali = szerokość obrazu w metrach."""
    linie = []
    for w in wyniki:
        linie += ["_.-IMAGEATTACH", f'"{_sciezka_cad(w.jpg)}"',
                  f"{w.xmin:.4f},{w.ymin:.4f}", f"{w.xmax - w.xmin:.4f}", "0"]
    linie += ["_.ZOOM", "_E"]
    with open(sciezka, "w", encoding="cp1250", errors="replace", newline="\r\n") as f:
        f.write("\n".join(linie) + "\n")


def zapisz_zestawienie(sciezka: str, wyniki: List[Wynik], uklad: Uklad, bufor: float,
                       zoom: int, url: str) -> None:
    with open(sciezka, "w", encoding="utf-8") as f:
        f.write(f"Układ: {uklad.nazwa} (EPSG:{uklad.epsg}), bufor {bufor:g} m, "
                f"OSM zoom {zoom}, źródło: {url}\n")
        f.write("Mapy: © OpenStreetMap contributors, licencja ODbL "
                "(https://www.openstreetmap.org/copyright)\n\n")
        f.write("nazwa;obiektów;Xmin;Ymin;Xmax;Ymax;piksel_m;szer_px;wys_px\n")
        for w in wyniki:
            f.write(f"{w.nazwa};{w.liczba_obiektow};{w.xmin:.2f};{w.ymin:.2f};{w.xmax:.2f};"
                    f"{w.ymax:.2f};{w.piksel:g};{w.szer_px};{w.wys_px}\n")


# ---------------------------------------------------------------------------
#  Program główny
# ---------------------------------------------------------------------------

def parametry(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    p = argparse.ArgumentParser(
        description="Pobiera mapy OpenStreetMap (JPG + JGW) wokół obiektów z pliku DXF "
                    "i przygotowuje je do wstawienia w GstarCAD.")
    p.add_argument("dxf", nargs="*", help="plik(i) DXF (bez podania - okno wyboru pliku)")
    p.add_argument("-o", "--wyjscie", help="katalog wynikowy (domyślnie <plik>_orientacje)")
    p.add_argument("-b", "--bufor", type=float, default=1500.0,
                   help="odległość od obiektu [m] (domyślnie 1500)")
    p.add_argument("-z", "--zoom", type=int, default=16,
                   help="poziom szczegółowości OSM: 15 (~1:25000), 16 (~1:10000, domyślnie), "
                        "17 (~1:5000)")
    p.add_argument("-p", "--piksel", type=float, default=None,
                   help="rozmiar piksela obrazu w metrach (domyślnie natywny dla zoomu)")
    p.add_argument("-g", "--grupuj", type=float, default=300.0,
                   help="łącz obiekty odległe o mniej niż N m w jeden obraz (domyślnie 300; "
                        "-1 = każdy element osobno)")
    p.add_argument("--jeden-obraz", action="store_true",
                   help="jeden obraz obejmujący wszystkie obiekty")
    p.add_argument("-w", "--warstwa", action="append",
                   help="uwzględnij tylko warstwę (można powtarzać, dozwolone * i ?)")
    p.add_argument("--typy", default=",".join(TYPY_DOMYSLNE),
                   help="typy encji DXF brane pod uwagę (po przecinku)")
    p.add_argument("--uklad", default="auto",
                   choices=["auto", "2000-5", "2000-6", "2000-7", "2000-8", "1992"],
                   help="układ współrzędnych rysunku (domyślnie wykrywany)")
    p.add_argument("--zamien-xy", action="store_true",
                   help="w rysunku X = północ, Y = wschód (zamiana osi)")
    p.add_argument("--url", default=DOMYSLNY_URL,
                   help="szablon adresu kafli {z}/{x}/{y} (opcjonalnie {s})")
    p.add_argument("--user-agent", default=USER_AGENT, help="nagłówek User-Agent")
    p.add_argument("--opoznienie", type=float, default=0.1,
                   help="minimalny odstęp między pobraniami kafli [s] (domyślnie 0.1)")
    p.add_argument("--cache", default=None,
                   help="katalog pamięci podręcznej kafli (domyślnie obok programu)")
    p.add_argument("--jakosc", type=int, default=90, help="jakość JPG 1-100 (domyślnie 90)")
    p.add_argument("--bez-podpisu", action="store_true",
                   help="nie umieszczaj na obrazie napisu © OpenStreetMap contributors")
    p.add_argument("--warstwa-cad", default="ORIENTACJA",
                   help="warstwa, na którą LISP wstawi obrazy (domyślnie ORIENTACJA)")
    p.add_argument("--max-mpx", type=float, default=250.0,
                   help="maksymalny rozmiar jednego obrazu w megapikselach (domyślnie 250)")
    p.add_argument("--max-kafli", type=int, default=3000,
                   help="maksymalna liczba kafli na jeden obraz (domyślnie 3000)")
    p.add_argument("--tylko-lista", action="store_true",
                   help="pokaż obiekty i zasięgi bez pobierania map")
    return p.parse_args(argv)


def wybierz_plik_okno() -> List[str]:
    try:
        import tkinter
        from tkinter import filedialog
        root = tkinter.Tk()
        root.withdraw()
        pliki = filedialog.askopenfilenames(title="Wybierz plik DXF",
                                            filetypes=[("DXF", "*.dxf"), ("Wszystkie", "*.*")])
        root.destroy()
        return list(pliki)
    except Exception:
        return []


def przetworz(sciezka: str, a: argparse.Namespace) -> None:
    print(f"\n== {sciezka}")
    typy = [t.strip() for t in a.typy.split(",") if t.strip()]
    obiekty = wczytaj_dxf(sciezka, typy, a.warstwa, a.zamien_xy)
    if not obiekty:
        print("   brak obiektów (sprawdź --warstwa / --typy)")
        return
    if a.uklad == "auto":
        uklad = wykryj_uklad(obiekty)
    elif a.uklad == "1992":
        uklad = UKLAD_1992
    else:
        uklad = uklad_2000(int(a.uklad.split("-")[1]))
    gk = GaussKruger(uklad)
    grupy = grupuj(obiekty, math.inf if a.jeden_obraz else a.grupuj)
    print(f"   obiektów DXF: {len(obiekty)}, warstwy: "
          f"{', '.join(sorted({o.warstwa for o in obiekty}))}")
    print(f"   układ: {uklad.nazwa} (EPSG:{uklad.epsg}), obrazów do utworzenia: {len(grupy)}")

    baza = os.path.splitext(os.path.basename(sciezka))[0]
    katalog = a.wyjscie or os.path.join(os.path.dirname(os.path.abspath(sciezka)),
                                        baza + "_orientacje")
    if a.tylko_lista:
        for i, g in enumerate(grupy, 1):
            x0, y0, x1, y1 = g.zasieg
            lat, lon = gk.do_geo((x0 + x1) / 2, (y0 + y1) / 2)
            print(f"   {i:3d}. elementów {len(g.obiekty):3d}  obiekt {x1 - x0:7.0f} x "
                  f"{y1 - y0:6.0f} m  obraz {x1 - x0 + 2 * a.bufor:7.0f} x "
                  f"{y1 - y0 + 2 * a.bufor:6.0f} m  środek {lat:.5f}N {lon:.5f}E")
        return

    os.makedirs(katalog, exist_ok=True)
    cache = a.cache or os.path.join(os.path.dirname(os.path.abspath(__file__)), "cache_kafli")
    kafle = Kafle(a.url, a.zoom, cache, a.opoznienie, a.user_agent)
    podpis = None if a.bez_podpisu else "© OpenStreetMap contributors"
    wyniki: List[Wynik] = []
    for i, g in enumerate(grupy, 1):
        x0, y0, x1, y1 = g.zasieg
        nazwa = f"{baza}_orientacja_{i:02d}"
        print(f" [{i}/{len(grupy)}] {nazwa}  ({len(g.obiekty)} el.)")
        wyniki.append(generuj_obraz(
            nazwa, (x0 - a.bufor, y0 - a.bufor, x1 + a.bufor, y1 + a.bufor), gk, kafle,
            a.piksel, katalog, a.jakosc, podpis, len(g.obiekty), int(a.max_mpx * 1e6),
            a.max_kafli))

    zapisz_lisp(os.path.join(katalog, baza + "_orientacje.lsp"), wyniki, a.warstwa_cad)
    zapisz_scr(os.path.join(katalog, baza + "_orientacje.scr"), wyniki)
    zapisz_zestawienie(os.path.join(katalog, baza + "_orientacje.txt"), wyniki, uklad,
                       a.bufor, a.zoom, a.url)
    print(f"   kafle: pobrane {kafle.pobrane}, z cache {kafle.z_cache}, błędy {kafle.bledy}")
    print(f"   wynik: {katalog}")
    print(f"   GstarCAD: APPLOAD -> {baza}_orientacje.lsp -> polecenie ORIENTACJE")


def main(argv: Optional[Sequence[str]] = None) -> int:
    a = parametry(argv)
    if Image is None and not a.tylko_lista:
        print("Brak biblioteki Pillow. Zainstaluj:  python -m pip install pillow")
        return 2
    pliki = a.dxf or wybierz_plik_okno()
    if not pliki:
        print("Nie wskazano pliku DXF.")
        return 1
    if not (0 <= a.zoom <= 19):
        print("Zoom musi być w zakresie 0-19.")
        return 1
    kod = 0
    for plik in pliki:
        try:
            przetworz(plik, a)
        except (ValueError, RuntimeError, OSError) as e:
            print(f"   BŁĄD: {e}")
            kod = 1
    if not a.dxf and sys.platform.startswith("win"):
        input("\nNaciśnij Enter, aby zamknąć...")
    return kod


if __name__ == "__main__":
    sys.exit(main())
