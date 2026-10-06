// Orientacje - generator podkładów mapowych OpenStreetMap do rysunków budowlanych.
//
// Działa w czystym Windows 10/11 - wymaga tylko wbudowanego .NET Framework 4.x.
// Kompilacja (robi to automatycznie orientacje.bat):
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /codepage:65001 /optimize+
//       /r:System.Drawing.dll /r:System.Windows.Forms.dll /out:orientacje.exe orientacje.cs
//
// Program czyta DXF (PL-2000 / PL-1992), dla każdego obiektu pobiera mapę OSM w zasięgu
// obiekt + bufor (domyślnie 1500 m), przelicza ją do układu rysunku i zapisuje JPG + JGW
// oraz plik LISP (polecenie ORIENTACJE) do wstawienia w GstarCAD.
//
// Kod zgodny z C# 5 (kompilator dostarczany z .NET Framework).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Orientacje")]
[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]

namespace Orientacje
{
    // ------------------------------------------------------------------
    //  Układy współrzędnych i odwzorowanie Gaussa-Krügera (GRS80)
    // ------------------------------------------------------------------

    class Uklad
    {
        public string Nazwa;
        public int Epsg;
        public double Lon0, K0, E0, N0;

        public static Uklad Pl2000(int strefa)
        {
            if (strefa < 5 || strefa > 8) throw new BladProgramu("Strefa PL-2000 musi być 5, 6, 7 lub 8");
            return new Uklad
            {
                Nazwa = "PL-2000 strefa " + strefa, Epsg = 2171 + strefa, Lon0 = 3.0 * strefa,
                K0 = 0.999923, E0 = strefa * 1000000.0 + 500000.0, N0 = 0.0
            };
        }

        public static Uklad Pl1992()
        {
            return new Uklad { Nazwa = "PL-1992", Epsg = 2180, Lon0 = 19.0, K0 = 0.9993, E0 = 500000.0, N0 = -5300000.0 };
        }
    }

    /// Odwzorowanie poprzeczne Merkatora - szeregi Krügera do n^4 (dokładność &lt;&lt; 1 mm).
    class GaussKruger
    {
        public readonly Uklad U;
        readonly double kA, c;
        readonly double[] alfa, beta, delta;

        public GaussKruger(Uklad u)
        {
            U = u;
            const double a = 6378137.0, f = 1.0 / 298.257222101;
            double n = f / (2.0 - f), n2 = n * n, n3 = n2 * n, n4 = n3 * n;
            double A = a / (1.0 + n) * (1.0 + n2 / 4.0 + n4 / 64.0);
            kA = u.K0 * A;
            c = 2.0 * Math.Sqrt(n) / (1.0 + n);
            alfa = new[] {
                n / 2 - 2.0 / 3 * n2 + 5.0 / 16 * n3 + 41.0 / 180 * n4,
                13.0 / 48 * n2 - 3.0 / 5 * n3 + 557.0 / 1440 * n4,
                61.0 / 240 * n3 - 103.0 / 140 * n4,
                49561.0 / 161280 * n4 };
            beta = new[] {
                n / 2 - 2.0 / 3 * n2 + 37.0 / 96 * n3 - 1.0 / 360 * n4,
                1.0 / 48 * n2 + 1.0 / 15 * n3 - 437.0 / 1440 * n4,
                17.0 / 480 * n3 - 37.0 / 840 * n4,
                4397.0 / 161280 * n4 };
            delta = new[] {
                2 * n - 2.0 / 3 * n2 - 2 * n3 + 116.0 / 45 * n4,
                7.0 / 3 * n2 - 8.0 / 5 * n3 - 227.0 / 45 * n4,
                56.0 / 15 * n3 - 136.0 / 35 * n4,
                4279.0 / 630 * n4 };
        }

        static double Atanh(double x) { return 0.5 * Math.Log((1.0 + x) / (1.0 - x)); }
        static double Rad(double d) { return d * Math.PI / 180.0; }
        static double Deg(double r) { return r * 180.0 / Math.PI; }

        /// (easting, northing) -> (szerokość, długość) [deg]
        public void DoGeo(double e, double nn, out double lat, out double lon)
        {
            double xi = (nn - U.N0) / kA, eta = (e - U.E0) / kA;
            double xi1 = xi, eta1 = eta;
            for (int j = 1; j <= 4; j++)
            {
                xi1 -= beta[j - 1] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
                eta1 -= beta[j - 1] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
            }
            double chi = Math.Asin(Math.Sin(xi1) / Math.Cosh(eta1));
            double phi = chi;
            for (int j = 1; j <= 4; j++) phi += delta[j - 1] * Math.Sin(2 * j * chi);
            lat = Deg(phi);
            lon = U.Lon0 + Deg(Math.Atan2(Math.Sinh(eta1), Math.Cos(xi1)));
        }

        /// (szerokość, długość) [deg] -> (easting, northing)
        public void ZGeo(double lat, double lon, out double e, out double nn)
        {
            double phi = Rad(lat), lam = Rad(lon - U.Lon0), s = Math.Sin(phi);
            double t = Math.Sinh(Atanh(s) - c * Atanh(c * s));
            double xi1 = Math.Atan2(t, Math.Cos(lam));
            double eta1 = Atanh(Math.Sin(lam) / Math.Sqrt(1.0 + t * t));
            double xi = xi1, eta = eta1;
            for (int j = 1; j <= 4; j++)
            {
                xi += alfa[j - 1] * Math.Sin(2 * j * xi1) * Math.Cosh(2 * j * eta1);
                eta += alfa[j - 1] * Math.Cos(2 * j * xi1) * Math.Sinh(2 * j * eta1);
            }
            e = U.E0 + kA * eta;
            nn = U.N0 + kA * xi;
        }
    }

    static class Merkator
    {
        public const int Kafel = 256;

        public static void Px(double lat, double lon, int zoom, out double x, out double y)
        {
            double skala = Kafel * Math.Pow(2, zoom);
            x = (lon + 180.0) / 360.0 * skala;
            double phi = lat * Math.PI / 180.0;
            y = (1.0 - Math.Log(Math.Tan(phi) + 1.0 / Math.Cos(phi)) / Math.PI) / 2.0 * skala;
        }

        public static double Rozdzielczosc(double lat, int zoom)
        {
            return 2 * Math.PI * 6378137.0 * Math.Cos(lat * Math.PI / 180.0) / (Kafel * Math.Pow(2, zoom));
        }
    }

    class BladProgramu : Exception
    {
        public BladProgramu(string m) : base(m) { }
    }

    // ------------------------------------------------------------------
    //  Odczyt DXF (ASCII)
    // ------------------------------------------------------------------

    class Obiekt
    {
        public string Typ, Warstwa, Uchwyt;
        public double Xmin = double.MaxValue, Ymin = double.MaxValue, Xmax = double.MinValue, Ymax = double.MinValue;
        public List<PointD[]> Linie = new List<PointD[]>(); // geometria do podglądu kontrolnego

        public void Dodaj(double x, double y, double r)
        {
            Xmin = Math.Min(Xmin, x - r); Ymin = Math.Min(Ymin, y - r);
            Xmax = Math.Max(Xmax, x + r); Ymax = Math.Max(Ymax, y + r);
        }

        public bool Poprawny { get { return Xmin <= Xmax && Ymin <= Ymax; } }
    }

    struct PointD
    {
        public double X, Y;
        public PointD(double x, double y) { X = x; Y = y; }
    }

    class Encja
    {
        public string Typ, Warstwa = "0", Uchwyt = "";
        public bool Papier, Zamknieta;
        public double R, ExtZ = 1.0, Kat0, Kat1 = 360.0;
        public double OsX, OsY;
        public List<PointD> Pkt = new List<PointD>();
        public List<PointD> Pkt2 = new List<PointD>();
        public double? X10, X11;
    }

    static class Dxf
    {
        public static readonly string[] TypyDomyslne =
            { "LWPOLYLINE", "POLYLINE", "LINE", "ARC", "CIRCLE", "POINT", "INSERT", "SPLINE", "ELLIPSE" };

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static double D(string s) { return double.Parse(s.Trim(), NumberStyles.Float, Inv); }

        public static List<Obiekt> Wczytaj(string sciezka, ICollection<string> typy, IList<Regex> warstwy, bool zamienXY)
        {
            using (var fs = File.OpenRead(sciezka))
            {
                var naglowek = new byte[18];
                int n = fs.Read(naglowek, 0, 18);
                if (n == 18 && Encoding.ASCII.GetString(naglowek) == "AutoCAD Binary DXF")
                    throw new BladProgramu("Binarny DXF nie jest obsługiwany - zapisz rysunek jako DXF ASCII.");
            }

            var typySet = new HashSet<string>(typy.Select(t => t.ToUpperInvariant()));
            var wynik = new List<Obiekt>();
            string sekcja = null;
            int poprzedniKod = -1;
            string poprzedniaWart = "";
            Encja akt = null, polilinia = null;

            Action<Encja> zapisz = enc =>
            {
                if (!typySet.Contains(enc.Typ) || enc.Papier) return;
                if (warstwy != null && warstwy.Count > 0 && !warstwy.Any(w => w.IsMatch(enc.Warstwa))) return;
                var ob = new Obiekt { Typ = enc.Typ, Warstwa = enc.Warstwa, Uchwyt = enc.Uchwyt };
                bool lustro = enc.ExtZ < 0 && (enc.Typ == "LWPOLYLINE" || enc.Typ == "CIRCLE" || enc.Typ == "ARC");
                Func<PointD, PointD> tr = p =>
                {
                    double x = lustro ? -p.X : p.X, y = p.Y;
                    return zamienXY ? new PointD(y, x) : new PointD(x, y);
                };
                var pkt = enc.Pkt.Select(tr).ToList();
                if (pkt.Count == 0) return;
                switch (enc.Typ)
                {
                    case "CIRCLE":
                    case "ARC":
                        {
                            var s = pkt[0];
                            ob.Dodaj(s.X, s.Y, enc.R);
                            double k0 = enc.Kat0, k1 = enc.Kat1;
                            if (enc.Typ == "CIRCLE") { k0 = 0; k1 = 360; }
                            if (k1 <= k0) k1 += 360;
                            var luk = new List<PointD>();
                            int kroki = Math.Max(8, (int)((k1 - k0) / 5));
                            for (int i = 0; i <= kroki; i++)
                            {
                                double a = (k0 + (k1 - k0) * i / kroki) * Math.PI / 180;
                                luk.Add(tr(new PointD(enc.Pkt[0].X + enc.R * Math.Cos(a), enc.Pkt[0].Y + enc.R * Math.Sin(a))));
                            }
                            ob.Linie.Add(luk.ToArray());
                            break;
                        }
                    case "ELLIPSE":
                        ob.Dodaj(pkt[0].X, pkt[0].Y, Math.Sqrt(enc.OsX * enc.OsX + enc.OsY * enc.OsY));
                        ob.Linie.Add(new[] { pkt[0] });
                        break;
                    case "POINT":
                    case "INSERT":
                        ob.Dodaj(pkt[0].X, pkt[0].Y, 0);
                        ob.Linie.Add(new[] { pkt[0] });
                        break;
                    case "LINE":
                        pkt = pkt.Take(1).Concat(enc.Pkt2.Take(1).Select(tr)).ToList();
                        goto default;
                    default:
                        foreach (var p in pkt) ob.Dodaj(p.X, p.Y, 0);
                        if (enc.Zamknieta && pkt.Count > 2) pkt.Add(pkt[0]);
                        ob.Linie.Add(pkt.ToArray());
                        break;
                }
                if (ob.Poprawny) wynik.Add(ob);
            };

            Action<Encja> zakoncz = enc =>
            {
                if (enc == null) return;
                if (enc.Typ == "VERTEX") { if (polilinia != null && enc.Pkt.Count > 0) polilinia.Pkt.Add(enc.Pkt[0]); return; }
                if (enc.Typ == "SEQEND") { if (polilinia != null) zapisz(polilinia); polilinia = null; return; }
                if (enc.Typ == "POLYLINE") { polilinia = enc; return; }
                zapisz(enc);
            };

            var latin1 = Encoding.GetEncoding(28591);
            using (var sr = new StreamReader(sciezka, latin1))
            {
                string kodTxt;
                while ((kodTxt = sr.ReadLine()) != null)
                {
                    string wart = sr.ReadLine();
                    if (wart == null) break;
                    kodTxt = kodTxt.Trim();
                    if (kodTxt.Length == 0) continue;
                    int kod;
                    if (!int.TryParse(kodTxt, NumberStyles.Integer, Inv, out kod))
                        throw new BladProgramu("Niepoprawna struktura DXF (kod grupy: '" + kodTxt + "')");
                    string w = wart.Trim();

                    if (kod == 0)
                    {
                        if (sekcja == "ENTITIES") zakoncz(akt);
                        akt = null;
                        if (w == "ENDSEC") sekcja = null;
                        else if (sekcja == "ENTITIES") akt = new Encja { Typ = w.ToUpperInvariant() };
                    }
                    else if (kod == 2 && poprzedniKod == 0 && poprzedniaWart == "SECTION")
                    {
                        sekcja = w.ToUpperInvariant();
                    }
                    else if (akt != null)
                    {
                        switch (kod)
                        {
                            case 8: akt.Warstwa = w; break;
                            case 5: akt.Uchwyt = w; break;
                            case 67: akt.Papier = w == "1"; break;
                            case 10: akt.X10 = D(w); break;
                            case 20:
                                if (akt.X10.HasValue) { akt.Pkt.Add(new PointD(akt.X10.Value, D(w))); akt.X10 = null; }
                                break;
                            case 11: akt.X11 = D(w); break;
                            case 21:
                                if (akt.X11.HasValue)
                                {
                                    if (akt.Typ == "ELLIPSE") { akt.OsX = akt.X11.Value; akt.OsY = D(w); }
                                    else akt.Pkt2.Add(new PointD(akt.X11.Value, D(w)));
                                    akt.X11 = null;
                                }
                                break;
                            case 40: if (akt.Typ == "CIRCLE" || akt.Typ == "ARC") akt.R = D(w); break;
                            case 50: if (akt.Typ == "ARC") akt.Kat0 = D(w); break;
                            case 51: if (akt.Typ == "ARC") akt.Kat1 = D(w); break;
                            case 70:
                                if (akt.Typ == "LWPOLYLINE" || akt.Typ == "POLYLINE")
                                {
                                    int fl;
                                    if (int.TryParse(w, NumberStyles.Integer, Inv, out fl)) akt.Zamknieta = (fl & 1) != 0;
                                }
                                break;
                            case 230: akt.ExtZ = D(w); break;
                        }
                    }
                    poprzedniKod = kod;
                    poprzedniaWart = w;
                }
            }
            if (sekcja == "ENTITIES") zakoncz(akt);
            if (polilinia != null) zapisz(polilinia);
            return wynik;
        }

        public static Uklad WykryjUklad(List<Obiekt> obiekty)
        {
            var xs = obiekty.Select(o => (o.Xmin + o.Xmax) / 2).OrderBy(v => v).ToList();
            var ys = obiekty.Select(o => (o.Ymin + o.Ymax) / 2).OrderBy(v => v).ToList();
            double x = xs[xs.Count / 2], y = ys[ys.Count / 2];
            int strefa = (int)Math.Floor(x / 1000000.0);
            if (strefa >= 5 && strefa <= 8 && y > 4000000 && y < 7000000) return Uklad.Pl2000(strefa);
            if (x > 100000 && x < 900000 && y > 100000 && y < 900000) return Uklad.Pl1992();
            throw new BladProgramu(string.Format(CultureInfo.InvariantCulture,
                "Nie rozpoznano układu współrzędnych (X={0:F0}, Y={1:F0}). Użyj opcji --uklad oraz ewentualnie --zamien-xy.", x, y));
        }
    }

    // ------------------------------------------------------------------
    //  Grupowanie obiektów
    // ------------------------------------------------------------------

    class Grupa
    {
        public List<Obiekt> Obiekty = new List<Obiekt>();
        public double Xmin { get { return Obiekty.Min(o => o.Xmin); } }
        public double Ymin { get { return Obiekty.Min(o => o.Ymin); } }
        public double Xmax { get { return Obiekty.Max(o => o.Xmax); } }
        public double Ymax { get { return Obiekty.Max(o => o.Ymax); } }

        static double Odl(Obiekt a, Obiekt b)
        {
            double dx = Math.Max(0.0, Math.Max(b.Xmin - a.Xmax, a.Xmin - b.Xmax));
            double dy = Math.Max(0.0, Math.Max(b.Ymin - a.Ymax, a.Ymin - b.Ymax));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// Łączy obiekty bliższe niż `odstep` (odstep &lt; 0 -> każdy osobno).
        public static List<Grupa> Grupuj(List<Obiekt> ob, double odstep)
        {
            int n = ob.Count;
            var rodzic = Enumerable.Range(0, n).ToArray();
            Func<int, int> znajdz = null;
            znajdz = i =>
            {
                while (rodzic[i] != i) { rodzic[i] = rodzic[rodzic[i]]; i = rodzic[i]; }
                return i;
            };
            if (odstep >= 0)
            {
                var kol = Enumerable.Range(0, n).OrderBy(i => ob[i].Xmin).ToArray();
                for (int p = 0; p < n; p++)
                {
                    var a = ob[kol[p]];
                    for (int q = p + 1; q < n; q++)
                    {
                        var b = ob[kol[q]];
                        if (b.Xmin > a.Xmax + odstep) break;
                        if (Odl(a, b) <= odstep)
                        {
                            int ri = znajdz(kol[p]), rj = znajdz(kol[q]);
                            if (ri != rj) rodzic[rj] = ri;
                        }
                    }
                }
            }
            var grupy = new Dictionary<int, Grupa>();
            for (int i = 0; i < n; i++)
            {
                int r = znajdz(i);
                Grupa g;
                if (!grupy.TryGetValue(r, out g)) { g = new Grupa(); grupy[r] = g; }
                g.Obiekty.Add(ob[i]);
            }
            // z północy na południe, z zachodu na wschód
            return grupy.Values.OrderBy(g => -Math.Round(g.Ymax / 100.0)).ThenBy(g => g.Xmin).ToList();
        }
    }

    // ------------------------------------------------------------------
    //  Kafle OSM
    // ------------------------------------------------------------------

    class Kafle
    {
        readonly string url, cache, userAgent;
        public readonly int Zoom;
        readonly double opoznienie;
        readonly Stopwatch zegar = Stopwatch.StartNew();
        double ostatni = -1e9;
        public int Pobrane, ZCache, Bledy;

        public Kafle(string url, int zoom, string cache, double opoznienie, string userAgent)
        {
            this.url = url; Zoom = zoom; this.cache = cache; this.opoznienie = opoznienie; this.userAgent = userAgent;
        }

        /// Zwraca piksele kafla 256x256 w formacie BGR (lub null).
        public byte[] Pobierz(int x, int y)
        {
            int n = 1 << Zoom;
            if (y < 0 || y >= n) return null;
            x = ((x % n) + n) % n;
            string plik = Path.Combine(cache, Zoom.ToString(), x.ToString(), y + ".img");
            if (File.Exists(plik))
            {
                var px = Dekoduj(File.ReadAllBytes(plik));
                if (px != null) { ZCache++; return px; }
            }
            string adres = url.Replace("{z}", Zoom.ToString()).Replace("{x}", x.ToString())
                              .Replace("{y}", y.ToString()).Replace("{s}", "abc"[(x + y) % 3].ToString());
            Exception blad = null;
            for (int proba = 0; proba < 4; proba++)
            {
                double czekaj = opoznienie - (zegar.Elapsed.TotalSeconds - ostatni);
                if (czekaj > 0) Thread.Sleep((int)(czekaj * 1000));
                ostatni = zegar.Elapsed.TotalSeconds;
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(adres);
                    req.UserAgent = userAgent;
                    req.Timeout = 30000;
                    req.ReadWriteTimeout = 30000;
                    if (req.Proxy != null) req.Proxy.Credentials = CredentialCache.DefaultCredentials;
                    byte[] dane;
                    using (var odp = (HttpWebResponse)req.GetResponse())
                    using (var s = odp.GetResponseStream())
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        dane = ms.ToArray();
                    }
                    var px = Dekoduj(dane);
                    if (px == null) throw new IOException("niepoprawny obraz kafla");
                    Directory.CreateDirectory(Path.GetDirectoryName(plik));
                    File.WriteAllBytes(plik, dane);
                    Pobrane++;
                    return px;
                }
                catch (WebException e)
                {
                    var odp = e.Response as HttpWebResponse;
                    if (odp != null)
                    {
                        int kod = (int)odp.StatusCode;
                        if (kod == 401 || kod == 403)
                            throw new BladProgramu("Serwer kafli odmówił dostępu (HTTP " + kod + ") dla " + adres +
                                ". Sprawdź zasady korzystania z serwera lub podaj inny --url.");
                        if (kod == 404) { blad = e; break; }
                    }
                    blad = e;
                }
                catch (IOException e) { blad = e; }
                Thread.Sleep(1000 << proba);
            }
            Console.WriteLine();
            Console.WriteLine("   ! nie pobrano kafla " + Zoom + "/" + x + "/" + y + ": " + (blad != null ? blad.Message : "?"));
            Bledy++;
            return null;
        }

        static byte[] Dekoduj(byte[] dane)
        {
            try
            {
                using (var ms = new MemoryStream(dane))
                using (var img = Image.FromStream(ms))
                using (var bmp = new Bitmap(img))
                {
                    if (bmp.Width != Merkator.Kafel || bmp.Height != Merkator.Kafel) return null;
                    return Obrazy.CzytajBgr(bmp);
                }
            }
            catch (ArgumentException) { return null; }
            catch (OutOfMemoryException) { return null; } // GDI+ zgłasza tak uszkodzone pliki
            catch (ExternalException) { return null; }
        }

        /// Mozaika kafli jako tablica BGR (szer = (tx1-tx0+1)*256).
        public byte[] Mozaika(int tx0, int ty0, int tx1, int ty1, out int szer, out int wys)
        {
            const int K = Merkator.Kafel;
            int nx = tx1 - tx0 + 1, ny = ty1 - ty0 + 1;
            szer = nx * K; wys = ny * K;
            var m = new byte[(long)szer * wys * 3];
            for (long i = 0; i < m.Length; i += 3) { m[i] = 233; m[i + 1] = 239; m[i + 2] = 242; } // tło OSM
            int razem = nx * ny, nr = 0;
            for (int ty = ty0; ty <= ty1; ty++)
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    nr++;
                    var k = Pobierz(tx, ty);
                    if (k != null)
                    {
                        long ox = (long)(tx - tx0) * K, oy = (long)(ty - ty0) * K;
                        for (int r = 0; r < K; r++)
                            Buffer.BlockCopy(k, r * K * 3, m, (int)(((oy + r) * szer + ox) * 3), K * 3);
                    }
                    if (nr % 25 == 0 || nr == razem) Console.Write("\r   kafle: " + nr + "/" + razem);
                }
            Console.WriteLine();
            return m;
        }
    }

    // ------------------------------------------------------------------
    //  Operacje na obrazach (GDI+)
    // ------------------------------------------------------------------

    static class Obrazy
    {
        public static byte[] CzytajBgr(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var wynik = new byte[w * h * 3];
                for (int y = 0; y < h; y++)
                    Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), wynik, y * w * 3, w * 3);
                return wynik;
            }
            finally { bmp.UnlockBits(bd); }
        }

        public static void ZapiszJpg(Bitmap bmp, string plik, int jakosc)
        {
            var kodek = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            using (var par = new EncoderParameters(1))
            {
                par.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)jakosc);
                bmp.Save(plik, kodek, par);
            }
        }

        /// Zmniejszenie mozaiki o całkowity współczynnik (uśrednianie) - antyaliasing.
        public static byte[] Zmniejsz(byte[] m, int w, int h, int f, out int w2, out int h2)
        {
            w2 = w / f; h2 = h / f;
            var o = new byte[(long)w2 * h2 * 3];
            int ff = f * f;
            for (int y = 0; y < h2; y++)
                for (int x = 0; x < w2; x++)
                    for (int c = 0; c < 3; c++)
                    {
                        int s = 0;
                        for (int dy = 0; dy < f; dy++)
                        {
                            long baza = ((long)(y * f + dy) * w + x * f) * 3 + c;
                            for (int dx = 0; dx < f; dx++) s += m[baza + dx * 3];
                        }
                        o[((long)y * w2 + x) * 3 + c] = (byte)(s / ff);
                    }
            return o;
        }
    }

    // ------------------------------------------------------------------
    //  Generowanie obrazu w układzie rysunku
    // ------------------------------------------------------------------

    class Wynik
    {
        public string Nazwa, Jpg;
        public double Xmin, Ymin, Xmax, Ymax, Piksel;
        public int Szer, Wys, LiczbaObiektow;
    }

    class Opcje
    {
        public List<string> Pliki = new List<string>();
        public string Wyjscie, Url = Program.DomyslnyUrl, UserAgent = Program.UserAgent, Cache;
        public string Uklad = "auto", WarstwaCad = "ORIENTACJA";
        public double Bufor = 1500, Grupuj = 300, Opoznienie = 0.1, MaxMpx = 250;
        public double? Piksel;
        public int Zoom = 16, Jakosc = 90, MaxKafli = 3000;
        public bool JedenObraz, ZamienXY, BezPodpisu, TylkoLista, Kontrola, Pomoc;
        public List<string> Warstwy = new List<string>();
        public List<string> Typy = Dxf.TypyDomyslne.ToList();
    }

    static class Generator
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static Wynik Utworz(string nazwa, double xmin, double ymin, double xmax, double ymax,
                                   GaussKruger gk, Kafle kafle, Opcje o, string katalog, int liczbaOb,
                                   List<Obiekt> geometria)
        {
            int zoom = kafle.Zoom;
            double latC, lonC;
            gk.DoGeo((xmin + xmax) / 2, (ymin + ymax) / 2, out latC, out lonC);
            double natywny = Merkator.Rozdzielczosc(latC, zoom);
            double piksel = o.Piksel.HasValue ? o.Piksel.Value : Math.Max(0.05, Math.Round(natywny, 2));

            xmin = Math.Floor(xmin / piksel) * piksel;
            ymin = Math.Floor(ymin / piksel) * piksel;
            int szer = (int)Math.Ceiling((xmax - xmin) / piksel);
            int wys = (int)Math.Ceiling((ymax - ymin) / piksel);
            if ((double)szer * wys > o.MaxMpx * 1e6 || szer > 65000 || wys > 65000)
                throw new BladProgramu(string.Format(Inv,
                    "Obraz {0} miałby {1}x{2} px ({3:F0} Mpx) - przekroczony limit --max-mpx. " +
                    "Zwiększ --piksel, zmniejsz --zoom lub zwiększ limit.", nazwa, szer, wys, szer * (double)wys / 1e6));
            xmax = xmin + szer * piksel;
            ymax = ymin + wys * piksel;

            // siatka węzłów: piksel wyjściowy -> globalny piksel Web Mercator
            const int krok = 64;
            var kol = new List<int>();
            for (int u = 0; u < szer; u += krok) kol.Add(u);
            kol.Add(szer);
            var wier = new List<int>();
            for (int v = 0; v < wys; v += krok) wier.Add(v);
            wier.Add(wys);
            var NX = new double[wier.Count, kol.Count];
            var NY = new double[wier.Count, kol.Count];
            double mxMin = double.MaxValue, myMin = double.MaxValue, mxMax = double.MinValue, myMax = double.MinValue;
            for (int j = 0; j < wier.Count; j++)
                for (int i = 0; i < kol.Count; i++)
                {
                    double lat, lon, mx, my;
                    gk.DoGeo(xmin + kol[i] * piksel, ymax - wier[j] * piksel, out lat, out lon);
                    Merkator.Px(lat, lon, zoom, out mx, out my);
                    NX[j, i] = mx; NY[j, i] = my;
                    mxMin = Math.Min(mxMin, mx); mxMax = Math.Max(mxMax, mx);
                    myMin = Math.Min(myMin, my); myMax = Math.Max(myMax, my);
                }
            const int K = Merkator.Kafel;
            int tx0 = (int)Math.Floor((mxMin - 2) / K), ty0 = (int)Math.Floor((myMin - 2) / K);
            int tx1 = (int)Math.Floor((mxMax + 2) / K), ty1 = (int)Math.Floor((myMax + 2) / K);
            int liczbaKafli = (tx1 - tx0 + 1) * (ty1 - ty0 + 1);
            if (liczbaKafli > o.MaxKafli)
                throw new BladProgramu("Obraz " + nazwa + " wymaga " + liczbaKafli + " kafli (limit --max-kafli " +
                    o.MaxKafli + "). Zmniejsz --zoom lub zwiększ limit (szanuj zasady serwera OSM).");
            Console.WriteLine(string.Format(Inv,
                "   zasięg {0:F0} x {1:F0} m, obraz {2} x {3} px (piksel {4} m, OSM zoom {5} ≈ {6:F2} m/px), kafli: {7}",
                xmax - xmin, ymax - ymin, szer, wys, piksel, zoom, natywny, liczbaKafli));

            int mw, mh;
            var moz = kafle.Mozaika(tx0, ty0, tx1, ty1, out mw, out mh);
            double ox = tx0 * (double)K, oy = ty0 * (double)K;
            int red = (int)(piksel / natywny);
            if (red >= 2) moz = Obrazy.Zmniejsz(moz, mw, mh, red, out mw, out mh);
            else red = 1;

            using (var bmp = new Bitmap(szer, wys, PixelFormat.Format24bppRgb))
            {
                var bd = bmp.LockBits(new Rectangle(0, 0, szer, wys), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try
                {
                    var wiersz = new byte[bd.Stride];
                    long mStride = (long)mw * 3;
                    for (int py = 0; py < wys; py++)
                    {
                        int j = Math.Min(py / krok, wier.Count - 2);
                        double fy = (py + 0.5 - wier[j]) / (wier[j + 1] - wier[j]);
                        for (int px = 0; px < szer; px++)
                        {
                            int i = Math.Min(px / krok, kol.Count - 2);
                            double fx = (px + 0.5 - kol[i]) / (kol[i + 1] - kol[i]);
                            double sx = (1 - fy) * ((1 - fx) * NX[j, i] + fx * NX[j, i + 1]) + fy * ((1 - fx) * NX[j + 1, i] + fx * NX[j + 1, i + 1]);
                            double sy = (1 - fy) * ((1 - fx) * NY[j, i] + fx * NY[j, i + 1]) + fy * ((1 - fx) * NY[j + 1, i] + fx * NY[j + 1, i + 1]);
                            // współrzędne w mozaice (środek piksela = k + 0.5)
                            double qx = (sx - ox) / red - 0.5, qy = (sy - oy) / red - 0.5;
                            int x0 = (int)Math.Floor(qx), y0 = (int)Math.Floor(qy);
                            double ax = qx - x0, ay = qy - y0;
                            if (x0 < 0) { x0 = 0; ax = 0; }
                            if (x0 > mw - 2) { x0 = mw - 2; ax = 1; }
                            if (y0 < 0) { y0 = 0; ay = 0; }
                            if (y0 > mh - 2) { y0 = mh - 2; ay = 1; }
                            long p00 = y0 * mStride + x0 * 3, p10 = p00 + 3, p01 = p00 + mStride, p11 = p01 + 3;
                            for (int c = 0; c < 3; c++)
                            {
                                double v = (1 - ay) * ((1 - ax) * moz[p00 + c] + ax * moz[p10 + c]) +
                                           ay * ((1 - ax) * moz[p01 + c] + ax * moz[p11 + c]);
                                wiersz[px * 3 + c] = (byte)(v + 0.5);
                            }
                        }
                        Marshal.Copy(wiersz, 0, new IntPtr(bd.Scan0.ToInt64() + (long)py * bd.Stride), bd.Stride);
                    }
                }
                finally { bmp.UnlockBits(bd); }
                moz = null;

                if (!o.BezPodpisu) Podpis(bmp, "© OpenStreetMap contributors");

                Directory.CreateDirectory(katalog);
                string jpg = Path.Combine(katalog, nazwa + ".jpg");
                Obrazy.ZapiszJpg(bmp, jpg, o.Jakosc);

                if (o.Kontrola)
                    Kontrola(bmp, Path.Combine(katalog, nazwa + "_kontrola.jpg"), geometria, xmin, ymax, piksel, o.Jakosc);

                File.WriteAllText(Path.Combine(katalog, nazwa + ".jgw"), string.Format(Inv,
                    "{0:F10}\r\n0.0000000000\r\n0.0000000000\r\n{1:F10}\r\n{2:F6}\r\n{3:F6}\r\n",
                    piksel, -piksel, xmin + piksel / 2, ymax - piksel / 2), Encoding.ASCII);
                File.WriteAllText(Path.Combine(katalog, nazwa + ".prj"), Prj(gk.U), Encoding.ASCII);

                return new Wynik
                {
                    Nazwa = nazwa, Jpg = jpg, Xmin = xmin, Ymin = ymin, Xmax = xmax, Ymax = ymax,
                    Piksel = piksel, Szer = szer, Wys = wys, LiczbaObiektow = liczbaOb
                };
            }
        }

        static void Podpis(Bitmap bmp, string tekst)
        {
            int rozmiar = Math.Max(14, Math.Min(bmp.Width, bmp.Height) / 110);
            using (var g = Graphics.FromImage(bmp))
            using (var font = new Font("Arial", rozmiar, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var r = g.MeasureString(tekst, font);
                int m = rozmiar / 3;
                float x = bmp.Width - r.Width - 2 * m, y = bmp.Height - r.Height - 2 * m;
                g.FillRectangle(Brushes.White, x - m, y - m, bmp.Width - x + m, bmp.Height - y + m);
                using (var pedzel = new SolidBrush(Color.FromArgb(40, 40, 40)))
                    g.DrawString(tekst, font, pedzel, x, y);
            }
        }

        /// Kopia obrazu z naniesionymi obiektami z DXF (czerwone) - do sprawdzenia dopasowania.
        static void Kontrola(Bitmap bmp, string plik, List<Obiekt> geometria, double xmin, double ymax, double piksel, int jakosc)
        {
            using (var k = new Bitmap(bmp))
            using (var g = Graphics.FromImage(k))
            using (var pioro = new Pen(Color.Red, Math.Max(2f, bmp.Width / 1500f)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (var ob in geometria)
                    foreach (var linia in ob.Linie)
                    {
                        var pts = linia.Select(p => new PointF((float)((p.X - xmin) / piksel), (float)((ymax - p.Y) / piksel))).ToArray();
                        if (pts.Length >= 2) g.DrawLines(pioro, pts);
                        else if (pts.Length == 1) g.DrawEllipse(pioro, pts[0].X - 4 * pioro.Width, pts[0].Y - 4 * pioro.Width, 8 * pioro.Width, 8 * pioro.Width);
                    }
                Obrazy.ZapiszJpg(k, plik, jakosc);
            }
        }

        public static string Prj(Uklad u)
        {
            return string.Format(Inv,
                "PROJCS[\"{0} (EPSG:{1})\",GEOGCS[\"ETRF2000-PL\",DATUM[\"ETRF2000_Poland\"," +
                "SPHEROID[\"GRS 1980\",6378137,298.257222101]],PRIMEM[\"Greenwich\",0]," +
                "UNIT[\"degree\",0.0174532925199433]],PROJECTION[\"Transverse_Mercator\"]," +
                "PARAMETER[\"latitude_of_origin\",0],PARAMETER[\"central_meridian\",{2}]," +
                "PARAMETER[\"scale_factor\",{3}],PARAMETER[\"false_easting\",{4:F0}]," +
                "PARAMETER[\"false_northing\",{5:F0}],UNIT[\"metre\",1]]",
                u.Nazwa, u.Epsg, u.Lon0, u.K0, u.E0, u.N0);
        }
    }

    // ------------------------------------------------------------------
    //  Pliki dla GstarCAD
    // ------------------------------------------------------------------

    static class PlikiCad
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Encoding Cp1250 = Encoding.GetEncoding(1250);

        static string Sc(string p) { return Path.GetFullPath(p).Replace('\\', '/'); }
        static string S(string t) { return "\"" + t.Replace('\\', '/').Replace("\"", "\\\"") + "\""; }

        public static void Lisp(string sciezka, List<Wynik> wyniki, string warstwa)
        {
            string nazwaLsp = Path.GetFileName(sciezka);
            var L = new List<string>
            {
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
                "                      (vlax-ename->vla-object (entlast))))))",
                "      (if obj",
                "        (progn",
                "          ;; skala niezależna od DPI zapisanego w JPG: szerokość = szer [m]",
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
                "  (if (findfile \"" + nazwaLsp + "\")",
                "    (strcat (vl-filename-directory (findfile \"" + nazwaLsp + "\")) \"/\")))",
                "(setq orient:nazwa-warstwy " + S(warstwa) + ")",
                "",
                "(defun c:ORIENTACJE ( / cmd)",
                "  (setq cmd (getvar \"CMDECHO\"))",
                "  (setvar \"CMDECHO\" 0)",
                "  (orient:warstwa orient:nazwa-warstwy)",
            };
            foreach (var w in wyniki)
                L.Add(string.Format(Inv, "  (orient:wstaw {0} {1:F4} {2:F4} {3:F4})", S(Sc(w.Jpg)), w.Xmin, w.Ymin, w.Xmax - w.Xmin));
            L.AddRange(new[]
            {
                "  (setvar \"CMDECHO\" cmd)",
                "  (command \"_.ZOOM\" \"_E\")",
                "  (princ \"\\nGotowe. Mapy: (c) OpenStreetMap contributors (ODbL).\")",
                "  (princ))",
                "",
                "(princ \"\\nWpisz ORIENTACJE aby wstawić podkłady.\")",
                "(princ)",
            });
            File.WriteAllText(sciezka, string.Join("\r\n", L) + "\r\n", Cp1250);
        }

        /// Skrypt (polecenie SCRIPT): wczytuje LISP i uruchamia ORIENTACJE.
        public static void Scr(string sciezka, string lsp)
        {
            File.WriteAllText(sciezka, "(load " + S(Sc(lsp)) + ")\r\nORIENTACJE\r\n", Cp1250);
        }

        public static void Zestawienie(string sciezka, List<Wynik> wyniki, Uklad u, Opcje o)
        {
            var sb = new StringBuilder();
            sb.AppendFormat(Inv, "Układ: {0} (EPSG:{1}), bufor {2} m, OSM zoom {3}, źródło: {4}\r\n", u.Nazwa, u.Epsg, o.Bufor, o.Zoom, o.Url);
            sb.Append("Mapy: © OpenStreetMap contributors, licencja ODbL (https://www.openstreetmap.org/copyright)\r\n\r\n");
            sb.Append("nazwa;obiektów;Xmin;Ymin;Xmax;Ymax;piksel_m;szer_px;wys_px\r\n");
            foreach (var w in wyniki)
                sb.AppendFormat(Inv, "{0};{1};{2:F2};{3:F2};{4:F2};{5:F2};{6};{7};{8}\r\n",
                    w.Nazwa, w.LiczbaObiektow, w.Xmin, w.Ymin, w.Xmax, w.Ymax, w.Piksel, w.Szer, w.Wys);
            File.WriteAllText(sciezka, sb.ToString(), new UTF8Encoding(true));
        }
    }

    // ------------------------------------------------------------------
    //  Program główny
    // ------------------------------------------------------------------

    static class Program
    {
        public const string Wersja = "2.0";
        public const string DomyslnyUrl = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
        public const string UserAgent = "Orientacje/" + Wersja +
            " (generator orientacji do rysunkow budowlanych; +https://github.com/cienki90/Orientacje)";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        [DllImport("kernel32.dll")]
        static extern uint GetConsoleProcessList(uint[] lista, uint liczba);

        const string Pomoc =
@"Orientacje " + Wersja + @" - mapy OpenStreetMap (JPG + JGW) wokół obiektów z DXF dla GstarCAD.

Użycie:  orientacje.bat [opcje] plik.dxf [plik2.dxf ...]
         (bez pliku - okno wyboru; można też przeciągnąć DXF na orientacje.bat)

  -o, --wyjscie KATALOG   katalog wynikowy (domyślnie <plik>_orientacje obok DXF)
  -b, --bufor M           odległość od obiektu [m] (domyślnie 1500)
  -z, --zoom N            szczegółowość OSM: 15 (~1:25000), 16 (~1:10000, domyślnie), 17 (~1:5000)
  -p, --piksel M          rozmiar piksela obrazu [m] (domyślnie natywny dla zoomu)
  -g, --grupuj M          łącz elementy bliższe niż M metrów w jeden obraz (domyślnie 300;
                          -1 = każdy element osobno)
      --jeden-obraz       jeden obraz dla całego rysunku
  -w, --warstwa NAZWA     tylko wskazana warstwa (można powtarzać, dozwolone * i ?)
      --typy LISTA        typy encji DXF (domyślnie LWPOLYLINE,POLYLINE,LINE,ARC,CIRCLE,
                          POINT,INSERT,SPLINE,ELLIPSE)
      --uklad U           auto | 2000-5 | 2000-6 | 2000-7 | 2000-8 | 1992 (domyślnie auto)
      --zamien-xy         w rysunku X = północ, Y = wschód
      --url SZABLON       serwer kafli {z}/{x}/{y} (opcjonalnie {s})
      --user-agent TEKST  nagłówek User-Agent
      --opoznienie S      minimalny odstęp między pobraniami kafli [s] (domyślnie 0.1)
      --cache KATALOG     pamięć podręczna kafli (domyślnie cache_kafli obok programu)
      --jakosc N          jakość JPG 1-100 (domyślnie 90)
      --bez-podpisu       bez napisu © OpenStreetMap contributors na obrazie
      --warstwa-cad NAZWA warstwa dla obrazów w GstarCAD (domyślnie ORIENTACJA)
      --kontrola          dodatkowo *_kontrola.jpg z naniesionymi obiektami DXF (czerwone)
      --max-mpx N         maks. rozmiar obrazu w Mpx (domyślnie 250)
      --max-kafli N       maks. liczba kafli na obraz (domyślnie 3000)
      --tylko-lista       tylko pokaż obiekty i zasięgi, bez pobierania
  -h, --help              ta pomoc
";

        static Opcje Parametry(string[] args)
        {
            var o = new Opcje();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                Func<string> nast = () =>
                {
                    if (i + 1 >= args.Length) throw new BladProgramu("Brak wartości dla opcji " + a);
                    return args[++i];
                };
                Func<double> liczba = () =>
                {
                    string s = nast().Replace(',', '.');
                    double v;
                    if (!double.TryParse(s, NumberStyles.Float, Inv, out v)) throw new BladProgramu("Niepoprawna liczba dla " + a + ": " + s);
                    return v;
                };
                switch (a)
                {
                    case "-h": case "--help": case "/?": o.Pomoc = true; break;
                    case "-o": case "--wyjscie": o.Wyjscie = nast(); break;
                    case "-b": case "--bufor": o.Bufor = liczba(); break;
                    case "-z": case "--zoom": o.Zoom = (int)liczba(); break;
                    case "-p": case "--piksel": o.Piksel = liczba(); break;
                    case "-g": case "--grupuj": o.Grupuj = liczba(); break;
                    case "--jeden-obraz": o.JedenObraz = true; break;
                    case "-w": case "--warstwa": o.Warstwy.Add(nast()); break;
                    case "--typy": o.Typy = nast().Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList(); break;
                    case "--uklad": o.Uklad = nast(); break;
                    case "--zamien-xy": o.ZamienXY = true; break;
                    case "--url": o.Url = nast(); break;
                    case "--user-agent": o.UserAgent = nast(); break;
                    case "--opoznienie": o.Opoznienie = liczba(); break;
                    case "--cache": o.Cache = nast(); break;
                    case "--jakosc": o.Jakosc = Math.Max(1, Math.Min(100, (int)liczba())); break;
                    case "--bez-podpisu": o.BezPodpisu = true; break;
                    case "--warstwa-cad": o.WarstwaCad = nast(); break;
                    case "--kontrola": o.Kontrola = true; break;
                    case "--max-mpx": o.MaxMpx = liczba(); break;
                    case "--max-kafli": o.MaxKafli = (int)liczba(); break;
                    case "--tylko-lista": o.TylkoLista = true; break;
                    default:
                        if (a.StartsWith("-") && a.Length > 1 && !File.Exists(a)) throw new BladProgramu("Nieznana opcja: " + a + " (pomoc: -h)");
                        o.Pliki.Add(a);
                        break;
                }
            }
            if (o.Zoom < 0 || o.Zoom > 19) throw new BladProgramu("Zoom musi być w zakresie 0-19.");
            if (o.Piksel.HasValue && o.Piksel.Value <= 0) throw new BladProgramu("--piksel musi być > 0.");
            return o;
        }

        static Regex Wzorzec(string glob)
        {
            return new Regex("^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                             RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        static string KatalogCache(Opcje o)
        {
            if (!string.IsNullOrEmpty(o.Cache)) return o.Cache;
            string obok = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache_kafli");
            try
            {
                Directory.CreateDirectory(obok);
                string test = Path.Combine(obok, ".zapis");
                File.WriteAllText(test, "");
                File.Delete(test);
                return obok;
            }
            catch (Exception)
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orientacje", "cache_kafli");
            }
        }

        static void Przetworz(string sciezka, Opcje o)
        {
            Console.WriteLine();
            Console.WriteLine("== " + sciezka);
            if (!File.Exists(sciezka)) throw new BladProgramu("Nie ma pliku: " + sciezka);
            var warstwy = o.Warstwy.Select(Wzorzec).ToList();
            var obiekty = Dxf.Wczytaj(sciezka, o.Typy, warstwy, o.ZamienXY);
            if (obiekty.Count == 0) { Console.WriteLine("   brak obiektów (sprawdź --warstwa / --typy)"); return; }

            Uklad u;
            if (o.Uklad == "auto") u = Dxf.WykryjUklad(obiekty);
            else if (o.Uklad == "1992") u = Uklad.Pl1992();
            else if (o.Uklad.StartsWith("2000-")) u = Uklad.Pl2000(int.Parse(o.Uklad.Substring(5), Inv));
            else throw new BladProgramu("Nieznany układ: " + o.Uklad);
            var gk = new GaussKruger(u);
            var grupy = Grupa.Grupuj(obiekty, o.JedenObraz ? double.MaxValue / 4 : o.Grupuj);
            Console.WriteLine("   obiektów DXF: " + obiekty.Count + ", warstwy: " +
                string.Join(", ", obiekty.Select(x => x.Warstwa).Distinct().OrderBy(x => x)));
            Console.WriteLine("   układ: " + u.Nazwa + " (EPSG:" + u.Epsg + "), obrazów do utworzenia: " + grupy.Count);

            string baza = Path.GetFileNameWithoutExtension(sciezka);
            string katalog = !string.IsNullOrEmpty(o.Wyjscie) ? o.Wyjscie
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sciezka)), baza + "_orientacje");

            if (o.TylkoLista)
            {
                for (int i = 0; i < grupy.Count; i++)
                {
                    var g = grupy[i];
                    double lat, lon;
                    gk.DoGeo((g.Xmin + g.Xmax) / 2, (g.Ymin + g.Ymax) / 2, out lat, out lon);
                    Console.WriteLine(string.Format(Inv,
                        "   {0,3}. elementów {1,3}  obiekt {2,7:F0} x {3,6:F0} m  obraz {4,7:F0} x {5,6:F0} m  środek {6:F5}N {7:F5}E",
                        i + 1, g.Obiekty.Count, g.Xmax - g.Xmin, g.Ymax - g.Ymin,
                        g.Xmax - g.Xmin + 2 * o.Bufor, g.Ymax - g.Ymin + 2 * o.Bufor, lat, lon));
                }
                return;
            }

            Directory.CreateDirectory(katalog);
            var kafle = new Kafle(o.Url, o.Zoom, KatalogCache(o), o.Opoznienie, o.UserAgent);
            var wyniki = new List<Wynik>();
            for (int i = 0; i < grupy.Count; i++)
            {
                var g = grupy[i];
                string nazwa = baza + "_orientacja_" + (i + 1).ToString("00");
                Console.WriteLine(" [" + (i + 1) + "/" + grupy.Count + "] " + nazwa + "  (" + g.Obiekty.Count + " el.)");
                wyniki.Add(Generator.Utworz(nazwa, g.Xmin - o.Bufor, g.Ymin - o.Bufor, g.Xmax + o.Bufor, g.Ymax + o.Bufor,
                    gk, kafle, o, katalog, g.Obiekty.Count, obiekty));
                GC.Collect();
            }
            string lsp = Path.Combine(katalog, baza + "_orientacje.lsp");
            PlikiCad.Lisp(lsp, wyniki, o.WarstwaCad);
            PlikiCad.Scr(Path.Combine(katalog, baza + "_orientacje.scr"), lsp);
            PlikiCad.Zestawienie(Path.Combine(katalog, baza + "_orientacje.txt"), wyniki, u, o);
            Console.WriteLine("   kafle: pobrane " + kafle.Pobrane + ", z cache " + kafle.ZCache + ", błędy " + kafle.Bledy);
            Console.WriteLine("   wynik: " + Path.GetFullPath(katalog));
            Console.WriteLine("   GstarCAD: APPLOAD -> " + baza + "_orientacje.lsp -> polecenie ORIENTACJE");
        }

        static List<string> OknoWyboru()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Wybierz plik(i) DXF";
                dlg.Filter = "Pliki DXF (*.dxf)|*.dxf|Wszystkie pliki (*.*)|*.*";
                dlg.Multiselect = true;
                return dlg.ShowDialog() == DialogResult.OK ? dlg.FileNames.ToList() : new List<string>();
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch (Exception) { }
            // TLS 1.2 / 1.3 dla starszych wersji .NET Framework
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 12288); }
            catch (NotSupportedException) { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }

            bool wlasnaKonsola = false;
            try { wlasnaKonsola = GetConsoleProcessList(new uint[4], 4) <= 1; } catch (Exception) { }
            int kod = 0;
            try
            {
                var o = Parametry(args);
                if (o.Pomoc) { Console.Write(Pomoc); return 0; }
                if (o.Pliki.Count == 0) o.Pliki = OknoWyboru();
                if (o.Pliki.Count == 0) { Console.WriteLine("Nie wskazano pliku DXF. Pomoc: orientacje -h"); kod = 1; }
                foreach (var plik in o.Pliki)
                {
                    try { Przetworz(plik, o); }
                    catch (BladProgramu e) { Console.WriteLine("   BŁĄD: " + e.Message); kod = 1; }
                    catch (IOException e) { Console.WriteLine("   BŁĄD: " + e.Message); kod = 1; }
                    catch (UnauthorizedAccessException e) { Console.WriteLine("   BŁĄD: " + e.Message); kod = 1; }
                    catch (OutOfMemoryException) { Console.WriteLine("   BŁĄD: za mało pamięci - zmniejsz --zoom lub zwiększ --piksel"); kod = 1; }
                }
            }
            catch (BladProgramu e) { Console.WriteLine("BŁĄD: " + e.Message); kod = 1; }
            if (wlasnaKonsola)
            {
                Console.WriteLine();
                Console.WriteLine("Naciśnij Enter, aby zamknąć...");
                Console.ReadLine();
            }
            return kod;
        }
    }
}
