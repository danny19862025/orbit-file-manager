"""Rebuilds OrbitIcons.ttf: the ~80 interface glyphs Orbit uses, taken from Microsoft's Fluent UI System Icons (MIT)
and placed at the same code points as Segoe Fluent Icons on Windows, so both apps share their glyph strings.
Usage: put FluentSystemIcons-Regular/Filled .ttf + .json (github.com/microsoft/fluentui-system-icons/tree/main/fonts)
in the current folder, then run:  python3 make-icon-font.py   (needs fontTools)."""
import json,sys
from fontTools.ttLib import TTFont
from fontTools import subset
M={'E70D':'chevron_down','E70E':'chevron_up','E70F':'edit','E710':'add','E711':'dismiss','E712':'more_horizontal',
'E713':'settings','E714':'video','E718':'pin','E71B':'link','E71C':'filter','E721':'search','E72A':'arrow_right',
'E72B':'arrow_left','E72C':'arrow_clockwise','E72D':'share','E72E':'lock_closed','E734':'star','E735':'star:filled',
'E73E':'checkmark','E74A':'arrow_up','E74C':'apps','E74D':'delete','E753':'cloud','E756':'window_console',
'E765':'keyboard','E768':'play','E769':'pause','E76C':'chevron_right','E774':'globe','E77F':'clipboard_paste',
'E785':'lock_open','E787':'calendar_ltr','E790':'color','E7A7':'arrow_undo','E7AC':'open','E7B3':'eye',
'E7B8':'box','E7BA':'warning','E7C3':'document','E7E8':'power','E7F4':'desktop','E80F':'home','E81E':'layer',
'E88E':'usb_stick','E894':'broom','E896':'arrow_download','E898':'arrow_upload','E89F':'panel_left_contract',
'E8A0':'panel_left_expand','E8A5':'document','E8A7':'window_new','E8A9':'grid','E8AB':'arrow_swap','E8AC':'rename',
'E8B3':'select_all_on','E8B7':'folder','E8BB':'dismiss','E8C6':'cut','E8C8':'copy','E8CB':'arrow_sort',
'E8CE':'text_font_size','E8D6':'music_note_2','E8DA':'folder_open','E8E5':'open','E8E6':'select_all_off','E8EC':'tag',
'E8F4':'folder_add','E8FD':'text_bullet_list_ltr','E909':'globe_search','E91B':'image','E921':'subtract',
'E922':'maximize','E923':'square_multiple','E939':'person_feedback','E943':'code','E946':'info','E958':'hard_drive',
'EC50':'folder_open','EDA2':'hard_drive'}
reg=json.load(open('FluentSystemIcons-Regular.json')); fil=json.load(open('FluentSystemIcons-Filled.json'))
R=TTFont('FluentSystemIcons-Regular.ttf'); F=TTFont('FluentSystemIcons-Filled.ttf')
rc=R.getBestCmap(); fc=F.getBestCmap()
pick={}
for k,v in M.items():
    style='regular'; name=v
    if ':' in v: name,style=v.split(':')
    j=fil if style=='filled' else reg
    for size in (20,24,16,28,32,48,12):
        key=f'ic_fluent_{name}_{size}_{style}'
        if key in j: pick[int(k,16)]=(style,j[key]); break
    else: print('MISSING',k,v); 
# Build: start from Regular, copy filled glyphs in as needed.
import copy
glyf=R['glyf']; hmtx=R['hmtx']
newcmap={}
for seg,(style,cp) in pick.items():
    if style=='regular': gname=rc[cp]
    else:
        src=fc[cp]; gname='filled_'+src
        glyf.glyphs[gname]=copy.deepcopy(F['glyf'][src]) if hasattr(F['glyf'],'glyphs') else F['glyf'][src]
        if gname not in R.getGlyphOrder(): R.setGlyphOrder(R.getGlyphOrder()+[gname])
        hmtx.metrics[gname]=F['hmtx'].metrics[src]
    newcmap[seg]=gname
for t in R['cmap'].tables:
    if t.isUnicode(): t.cmap=dict(newcmap)
R['maxp'].numGlyphs=len(R.getGlyphOrder())
R.save('tmp.ttf')
f=TTFont('tmp.ttf')
opts=subset.Options(); opts.name_IDs=['*']; opts.notdef_outline=True; opts.layout_features=[]
s=subset.Subsetter(opts); s.populate(unicodes=list(newcmap)); s.subset(f)
for rec in f['name'].names:
    if rec.nameID in (1,16): rec.string='Orbit Icons'
    elif rec.nameID==4: rec.string='Orbit Icons Regular'
    elif rec.nameID==6: rec.string='OrbitIcons-Regular'
f.save('OrbitIcons.ttf'); print('ok',len(newcmap))
