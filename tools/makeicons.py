"""Generuje spojny zestaw ikon .ico dla czterech .exe i zapisuje je do src/*/Assets/.
Wspolny ciemny kafelek + motyw wezla/polaczenia, rozniony glifem i kolorem akcentu.
Wymaga tylko Pillow (PIL). Uruchom z korzenia repo:  python tools/makeicons.py
"""
from PIL import Image, ImageDraw, ImageChops, ImageFilter
from pathlib import Path
import math

SS = 4
BASE = 1024
S = BASE * SS
C = S // 2
ROOT = Path(__file__).resolve().parent.parent
TARGET = {
    "avh":        ROOT/"src/AgentVirtualHand/Assets/avh.ico",
    "avh-hub":    ROOT/"src/AgentVirtualHand.Hub/Assets/avh-hub.ico",
    "avh-link":   ROOT/"src/AgentVirtualHand.Link/Assets/avh-link.ico",
    "avh-assist": ROOT/"src/AgentVirtualHand.Assist/Assets/avh-assist.ico",
}
ICO_SIZES = [(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)]

def lerp(a,b,t): return tuple(round(a[i]+(b[i]-a[i])*t) for i in range(3))

def tile():
    top, bot = (0x19,0x21,0x2E), (0x0A,0x0D,0x12)
    g = Image.new("RGB",(S,S)); px=g.load()
    for y in range(S):
        col=lerp(top,bot,y/(S-1))
        for x in range(S): px[x,y]=col
    img=Image.new("RGBA",(S,S),(0,0,0,0))
    m=int(S*0.085); rad=int(S*0.21)
    mask=Image.new("L",(S,S),0)
    ImageDraw.Draw(mask).rounded_rectangle([m,m,S-m,S-m],radius=rad,fill=255)
    img.paste(g,(0,0),mask)
    d=ImageDraw.Draw(img)
    d.rounded_rectangle([m,m,S-m,S-m],radius=rad,outline=(0x2A,0x34,0x45),width=max(2,int(S*0.004)))
    sheen=Image.new("RGBA",(S,S),(0,0,0,0))
    ImageDraw.Draw(sheen).rounded_rectangle([m,m,S-m,int(S*0.5)],radius=rad,fill=(255,255,255,16))
    return Image.alpha_composite(img, Image.composite(sheen,Image.new("RGBA",(S,S),(0,0,0,0)),mask))

def glow(img,color,r):
    g=Image.new("RGBA",(S,S),(0,0,0,0))
    ImageDraw.Draw(g).ellipse([C-r,C-r,C+r,C+r],fill=color+(90,))
    return Image.alpha_composite(img, g.filter(ImageFilter.GaussianBlur(int(S*0.045))))

def dot(d,cx,cy,r,f): d.ellipse([cx-r,cy-r,cx+r,cy+r],fill=f)
def ring(d,cx,cy,r,w,f): d.ellipse([cx-r,cy-r,cx+r,cy+r],outline=f,width=w)
def line(d,p1,p2,w,f):
    d.line([p1,p2],fill=f,width=w)
    for p in (p1,p2): d.ellipse([p[0]-w//2,p[1]-w//2,p[0]+w//2,p[1]+w//2],fill=f)

def draw_host(d,a):
    ring(d,C,C,int(S*0.265),int(S*0.045),a); ring(d,C,C,int(S*0.175),int(S*0.045),a); dot(d,C,C,int(S*0.072),a)
def draw_hub(d,a):
    dist=int(S*0.235); rs=int(S*0.052); w=int(S*0.040)
    pts=[(C,C-dist),(C,C+dist),(C-dist,C),(C+dist,C)]
    for p in pts: line(d,(C,C),p,w,a)
    for p in pts: dot(d,p[0],p[1],rs,a)
    dot(d,C,C,int(S*0.078),a)
def draw_link(d,a):
    dx=int(S*0.185); r=int(S*0.082); w=int(S*0.052)
    line(d,(C-dx,C),(C+dx,C),w,a); dot(d,C-dx,C,r,a); dot(d,C+dx,C,r,a)
def draw_assist(d,a):
    ring(d,C,C,int(S*0.235),int(S*0.088),a); dot(d,C,C,int(S*0.075),a)
def assist_gap_mask():
    m=Image.new("L",(S,S),0); md=ImageDraw.Draw(m)
    R=int(S*0.235); gw=int(S*0.072); inr=R-int(S*0.085); out=R+int(S*0.085)
    for ang in (45,135,225,315):
        rad=math.radians(ang)
        p1=(C+int(inr*math.cos(rad)),C+int(inr*math.sin(rad)))
        p2=(C+int(out*math.cos(rad)),C+int(out*math.sin(rad)))
        md.line([p1,p2],fill=255,width=gw)
        for p in (p1,p2): md.ellipse([p[0]-gw//2,p[1]-gw//2,p[0]+gw//2,p[1]+gw//2],fill=255)
    return m

APPS={"avh":((0x4C,0x8D,0xFF),draw_host),"avh-hub":((0x5B,0xD3,0x9A),draw_hub),
      "avh-link":((0x56,0xC6,0xF5),draw_link),"avh-assist":((0xFF,0xB4,0x54),draw_assist)}

for name,(accent,fn) in APPS.items():
    img=glow(tile(),accent,int(S*0.30))
    layer=Image.new("RGBA",(S,S),(0,0,0,0)); fn(ImageDraw.Draw(layer),accent)
    if name=="avh-assist":
        r,g,b,al=layer.split(); al=ImageChops.subtract(al,assist_gap_mask()); layer=Image.merge("RGBA",(r,g,b,al))
    img=Image.alpha_composite(img,layer).resize((BASE,BASE),Image.LANCZOS)
    out=TARGET[name]; out.parent.mkdir(parents=True,exist_ok=True)
    img.save(out,sizes=ICO_SIZES)
    print("ok",out.relative_to(ROOT))
