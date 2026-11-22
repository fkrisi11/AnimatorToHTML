(function(){
  const gmap={};DATA.graphs.forEach(g=>gmap[g.id]=g);
  const clipmap={};(DATA.clips||[]).forEach(c=>clipmap[c.id]=c);
  const view_el=document.getElementById('view'),vp=document.getElementById('vp');
  const detail=document.getElementById('detail'),crumbsEl=document.getElementById('crumbs');
  const listEl=document.getElementById('list'),searchEl=document.getElementById('search'),pop=document.getElementById('pop');
  const animwin=document.getElementById('animwin'),animbody=document.getElementById('animbody'),animtitle=document.getElementById('animtitle');
  let stack=[],curGraph=null,tf={x:0,y:0,k:1},tab='layers',groupsById={},selEl=null,btRecompute=null;
  const HINT=`<p class='hint'>Click a state or transition. Middle-mouse/drag to pan, wheel to zoom, F to fit, click empty space to deselect. Double-click a sub-state-machine or blend-tree state to drill in.</p>`;
  const ABBR={Bool:'BOL',Int:'INT',Float:'FLT',Trigger:'TRG'};
  function esc(s){return (s==null?'':(''+s)).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&#34;').replace(/'/g,'&#39;');}
  function num(v){return ''+(Math.round(v*1000)/1000);}
  // A polygon with each corner replaced by a circular arc. Mirrors AnimatorToolkit.RoundedPolygon in
  // Core/AnimatorToolkit_Style.cs - same setback rule (radius/tan(theta/2), clamped to half the shorter
  // adjacent edge so neighbouring corners cannot eat into each other) and the same shorter-way-round sweep.
  // Returns the input untouched when there is nothing to round.
  function roundPoly(pts,r,seg){
    if(!(r>0.001)||seg<1)return pts;
    const n=pts.length,out=[];
    for(let i=0;i<n;i++){
      const p=pts[i],a=pts[(i-1+n)%n],b=pts[(i+1)%n];
      let v1=[a[0]-p[0],a[1]-p[1]],v2=[b[0]-p[0],b[1]-p[1]];
      const l1=Math.hypot(v1[0],v1[1]),l2=Math.hypot(v2[0],v2[1]);
      if(l1<1e-4||l2<1e-4){out.push(p);continue;}
      v1=[v1[0]/l1,v1[1]/l1];v2=[v2[0]/l2,v2[1]/l2];
      const cos=Math.max(-1,Math.min(1,v1[0]*v2[0]+v1[1]*v2[1]));
      if(cos<-0.9999){out.push(p);continue;}
      const half=Math.acos(cos)/2,tn=Math.tan(half),sn=Math.sin(half);
      if(tn<1e-4||sn<1e-4){out.push(p);continue;}
      let bx=v1[0]+v2[0],by=v1[1]+v2[1];
      const bl=Math.hypot(bx,by);
      if(bl<1e-4){out.push(p);continue;}
      bx/=bl;by/=bl;
      const d=Math.min(r/tn,Math.min(l1,l2)/2),rr=d*tn;
      const t1=[p[0]+v1[0]*d,p[1]+v1[1]*d],t2=[p[0]+v2[0]*d,p[1]+v2[1]*d];
      const cx=p[0]+bx*(rr/sn),cy=p[1]+by*(rr/sn);
      const a1=Math.atan2(t1[1]-cy,t1[0]-cx),a2=Math.atan2(t2[1]-cy,t2[0]-cx);
      let sw=a2-a1;
      while(sw>Math.PI)sw-=2*Math.PI;
      while(sw<-Math.PI)sw+=2*Math.PI;
      for(let k=0;k<=seg;k++){const g=a1+sw*k/seg;out.push([cx+Math.cos(g)*rr,cy+Math.sin(g)*rr]);}
    }
    return out;
  }

  function isNum(v){return /^-?\d+(\.\d+)?$/.test(v);}
  function cb(v){return `<input type='checkbox' disabled ${v?'checked':''}>`;}
  function nf(v){return `<span class='nf'>${esc(v)}</span>`;}
  function fmtVal(v){return (v==='true'||v==='false')?cb(v==='true'):(isNum(v)?nf(v):`<span class='rt'>${esc(v)}</span>`);}
  function applyTf(){vp.setAttribute('transform','translate('+tf.x+','+tf.y+') scale('+tf.k+')');}

  const themeBtn=document.getElementById('theme');
  let light=window.matchMedia&&window.matchMedia('(prefers-color-scheme: light)').matches;
  function applyTheme(){document.body.classList.toggle('light',light);themeBtn.textContent=light?'Dark':'Light';}
  applyTheme();themeBtn.addEventListener('click',()=>{light=!light;applyTheme();if(btRecompute)btRecompute();});

  function rzH(el,target,left){el.addEventListener('mousedown',e=>{e.preventDefault();const sx=e.clientX,sw=target.getBoundingClientRect().width;
    function mm(ev){let w=left?sw+(ev.clientX-sx):sw-(ev.clientX-sx);w=Math.max(150,Math.min(700,w));target.style.flex='0 0 '+w+'px';target.style.width=w+'px';}
    function mu(){window.removeEventListener('mousemove',mm);window.removeEventListener('mouseup',mu);}window.addEventListener('mousemove',mm);window.addEventListener('mouseup',mu);});}
  rzH(document.getElementById('rz1'),document.getElementById('side'),true);rzH(document.getElementById('rz2'),detail,false);
  let animPrevH=280;
  (function(){const h=document.getElementById('animhandle');h.addEventListener('mousedown',e=>{e.preventDefault();const sy=e.clientY,sh=animwin.getBoundingClientRect().height;
    function mm(ev){let hh=sh-(ev.clientY-sy);hh=Math.max(34,Math.min(window.innerHeight-80,hh));animwin.style.height=hh+'px';}
    function mu(){window.removeEventListener('mousemove',mm);window.removeEventListener('mouseup',mu);}window.addEventListener('mousemove',mm);window.addEventListener('mouseup',mu);});})();
  document.getElementById('animclose').addEventListener('click',e=>{e.stopPropagation();hideAnim();});
  // Click the title bar to collapse to just the bar, or restore the previous height.
  document.getElementById('animbar').addEventListener('click',()=>{const barH=34,cur=animwin.getBoundingClientRect().height;if(cur>barH+8){animPrevH=cur;animwin.style.height=barH+'px';}else{animwin.style.height=(animPrevH||280)+'px';}});
  function hideAnim(){animwin.style.display='none';}

  document.querySelectorAll('.tab').forEach(t=>t.addEventListener('click',()=>{tab=t.dataset.tab;document.querySelectorAll('.tab').forEach(x=>x.classList.toggle('sel',x===t));searchEl.value='';renderList();}));
  searchEl.addEventListener('input',renderList);
  function pval(p){return p.type==='Bool'?cb(p.def==='true'):(p.type==='Trigger'?'':nf(p.def));}
  // One badge for the layer's WD state and its blend-tree ownership: an exclamation mark when either is
  // wrong (the tooltip says which, or both), the plain green WD only when an all-on layer has nothing
  // else to report. NOTE: this block lives inside a C# verbatim string - a bare double quote ends it.
  function lbadges(l){let b='';if(l.none)b+=`<span class='lb lb-none' title='${esc(l.noneTip||'')}'>None</span>`;
    if(l.wd==='mixed'||l.wdo||l.bad||l.btr)b+=`<span class='lb lb-wdmix' title='${esc(l.wdTip||'')}'>!</span>`;
    else if(l.wd==='on')b+=`<span class='lb lb-wdon' title='${esc(l.wdTip||'')}'>WD</span>`;if(l.idm)b+=`<span class='lb lb-id' title='Has identifier transitions'>ID</span>`;if(l.mask)b+=`<span class='lb lb-m' title='${esc(l.mask)}'>M</span>`;return b;}
  function renderList(){const q=searchEl.value.trim().toLowerCase();
    if(tab==='layers') listEl.innerHTML=DATA.layers.map((l,i)=>(q&&!l.name.toLowerCase().includes(q))?'':`<div class='row lrow'><div class='lmain' data-i='${i}'><div class='ltop'><span class='lix'>${l.index}</span><span class='lname'>${esc(l.name)}</span><span class='lbadges'>${lbadges(l)}</span></div><div class='wbar'><div class='wfill' style='width:${Math.max(0,Math.min(1,l.weight))*100}%'></div></div></div><button class='cog' data-cog='${i}' title='Layer settings'>⚙</button></div>`).join('')||`<p class='hint'>No layers.</p>`;
    else listEl.innerHTML=DATA.parameters.map((p,i)=>(q&&!(p.name+' '+p.type).toLowerCase().includes(q))?'':`<button class='row prow' data-p='${i}'><span class='pname'>${esc(p.name)}</span><span class='ptags'><span class='tb tb-${p.type.toLowerCase()}'>${ABBR[p.type]||'?'}</span>${p.vrc?`<span class='tb vrc'>VRC</span>`:''}${p.mismatch?`<span class='tb mm' title='Type mismatch vs VRChat'>&ne;</span>`:''}${pval(p)}</span></button>`).join('')||`<p class='hint'>No parameters.</p>`;
  }
  function setSel(el){document.querySelectorAll('.row').forEach(x=>x.classList.remove('sel'));if(el)el.classList.add('sel');}
  listEl.addEventListener('click',e=>{const cog=e.target.closest('.cog');if(cog){e.stopPropagation();showLayerPop(DATA.layers[+cog.dataset.cog],cog);return;}
    const lm=e.target.closest('.lmain');if(lm){setSel(lm.closest('.lrow'));selectLayer(+lm.dataset.i);return;}
    const pr=e.target.closest('.prow');if(pr){setSel(pr);showParam(DATA.parameters[+pr.dataset.p]);return;}});
  function layerHtml(l){return `<div class='fld'><span>Weight</span><div style='display:flex;align-items:center;gap:8px'><div class='wbar big' style='width:90px'><div class='wfill' style='width:${Math.max(0,Math.min(1,l.weight))*100}%'></div></div>${nf(num(l.weight))}</div></div>`
    +`<div class='fld'><span>Mask</span><span class='rt'>${esc(l.mask||'None')}</span></div><div class='fld'><span>Blending mode</span><span class='rt'>${esc(l.blending)}</span></div>`
    +`<div class='fld'><span>Sync</span>${cb(l.sync)}</div><div class='fld'><span>Timing</span>${cb(l.timing)}</div><div class='fld'><span>IK Pass</span>${cb(l.ik)}</div>`;}
  function showLayerPop(l,anchor){hideAnim();pop.innerHTML=`<h2>Layer Settings: ${esc(l.name)}</h2>`+layerHtml(l);const r=anchor.getBoundingClientRect();pop.style.display='block';
    let lft=r.right+8;if(lft+pop.offsetWidth>window.innerWidth-8)lft=Math.max(8,r.left-pop.offsetWidth-8);pop.style.left=lft+'px';
    pop.style.top=Math.max(8,Math.min(r.top,window.innerHeight-pop.offsetHeight-8))+'px';} // clamp to viewport using the real popup size
  document.addEventListener('mousedown',e=>{if(pop.style.display==='block'&&!pop.contains(e.target)&&!e.target.closest('.cog'))pop.style.display='none';});

  function selectLayer(i){hideAnim();const l=DATA.layers[i];if(!l||!l.root){curGraph=null;vp.innerHTML='';crumbsEl.textContent='(empty layer)';return;}stack=[{id:l.root,title:l.name}];showGraph(true);}
  function enter(id){const g=gmap[id];if(!g)return;stack.push({id:id,title:g.title});showGraph(true);}
  function popTo(i){stack=stack.slice(0,i+1);showGraph(true);}
  function showGraph(doFit){const top=stack[stack.length-1];curGraph=gmap[top.id];if(!curGraph){vp.innerHTML='';return;}
    // No separator between them any more: the chevrons interlock, so the shape IS the separator.
    crumbsEl.innerHTML=stack.map((s,i)=>`<span class='crumb ${i===0?'first':''} ${i===stack.length-1?'last':''}' data-i='${i}'>${esc(s.title)}</span>`).join('');renderSvg(curGraph);wireBtNodes(curGraph);selEl=null;if(doFit)fit();}
  crumbsEl.addEventListener('click',e=>{const c=e.target.closest('.crumb');if(c)popTo(+c.dataset.i);});

  function renderSvg(g){const nm={};g.nodes.forEach(n=>nm[n.id]=n);groupsById={};const groups={};
    g.edges.forEach((ed,i)=>{if(ed.kind==='id')return;const k=ed.fromId+'|'+ed.toId;(groups[k]||(groups[k]={fromId:ed.fromId,toId:ed.toId,idx:[]})).idx.push(i);});
    // Grid sized to the node bounds (+margin) so it never runs out, even on very wide blend-tree layers.
    let bx0=0,by0=0,bx1=0,by1=0;if(g.nodes.length){bx0=bx1=g.nodes[0].x;by0=by1=g.nodes[0].y;g.nodes.forEach(n=>{bx0=Math.min(bx0,n.x);by0=Math.min(by0,n.y);bx1=Math.max(bx1,n.x+n.w);by1=Math.max(by1,n.y+n.h);});}
    const gp=4000;
    const keys=new Set(Object.keys(groups));let gi=0;let e=`<rect class='gridbg' x='${bx0-gp}' y='${by0-gp}' width='${(bx1-bx0)+gp*2}' height='${(by1-by0)+gp*2}' fill='url(#grid)'/>`;
    for(const k in groups){const gr=groups[k];gr.gid='grp'+(gi++);groupsById[gr.gid]=gr;
      const a=nm[gr.fromId],b=nm[gr.toId];if(!a||!b)continue;const ges=gr.idx.map(i=>g.edges[i]);const multi=ges.length>1;const kind=ges[0].kind||'';
      // Parent-owned transitions (Unity's 'selector' kind) dim the whole edge when they are ALL of it,
      // and mark the middle head only when the bundle is mixed - the same rule the canvas draws at three
      // heads, which is also why a bundle now shows three chevrons rather than two.
      const allpo=ges.every(x=>x.po),mixed=ges.some(x=>x.po)&&ges.some(x=>!x.po);
      let mk;
      if(kind==='default')mk=multi?'arrowtri':'arroworange';
      else if(allpo)mk=multi?'arrowtridim':'arrowdim';
      else if(mixed&&multi)mk='arrowtrimixed';
      else if(kind==='state'&&ges.every(x=>!x.hasExit))mk=multi?'arrowtri':'arrowblue';
      else mk=multi?'arrowtri':'arrow';
      const mksel=multi?'arrowtrisel':'arrowsel';let d,mx,my,att='marker-mid',stub=false,wx=0,wy=0,tol='';
      // Self-transitions are the ARROW look, never the loop: an arrowhead sitting under the state and
      // pointing back up into it. That is what the community reads as a self-transition, and it is what
      // the canvas draws. A marker needs a path to sit on and to take its direction from, so the path is
      // a short upward stub with its stroke hidden ('stub') - only the head shows. The hit path keeps the
      // same geometry, so the arrow is still clickable through its fat transparent stroke.
      if(gr.fromId===gr.toId){const cx=a.x+a.w/2,by=a.y+a.h;d=`M ${cx} ${by+16} L ${cx} ${by+8}`;mx=cx;my=by+8;att='marker-end';stub=true;wx=cx+32;wy=by+12;}
      else{const ac={x:a.x+a.w/2,y:a.y+a.h/2},bc={x:b.x+b.w/2,y:b.y+b.h/2};let dx=bc.x-ac.x,dy=bc.y-ac.y;const L=Math.hypot(dx,dy)||1;dx/=L;dy/=L;
        // Unity's own edge geometry, the same as BundleLineGeometry on the canvas. CENTRE TO CENTRE with
        // no clipping to the node shapes: the line runs right into both nodes, and the nodes - appended
        // after the edges, so they paint over them - cover the ends. That is what makes a fan leaving one
        // state converge on a single point instead of sliding along its edge.
        //
        // The 5px sideways nudge goes on EVERY edge, not only on a reversed pair. It comes from the
        // direction, so A->B and B->A take it in opposite senses and come to rest side by side.
        const ox=-dy*5,oy=dx*5;
        const p1={x:ac.x+ox,y:ac.y+oy},p2={x:bc.x+ox,y:bc.y+oy};
        // The arrowhead is a marker-mid sitting on the path's middle vertex, so moving that vertex moves
        // the head. Half a nudge back from the midpoint, which is what makes an incoming and an outgoing
        // head read as a pair rather than as two unrelated marks.
        mx=(p1.x+p2.x)/2+dy*0.5;my=(p1.y+p2.y)/2-dx*0.5;
        // Perpendicular to the LINE, not straight up: a fixed upward nudge lands on the arrowheads the
        // moment the edge runs vertically, which is most of a tidied graph. Matches the canvas exactly.
        wx=mx-dy*12;wy=my+dx*12;
        if(g.kind==='bt'){
          // The blend tree canvas's connection line: cubic bezier, horizontal tangents, half the
          // horizontal gap clamped to 25..100 - the same numbers as EdgeControlPoints in the canvas.
          // Anchored right-edge to left-edge rather than centre-to-centre like the state graphs,
          // because a blend tree always runs parent-right into child-left and the tangents assume it.
          // This graph is laid out by the exporter, not copied from the canvas, so it is the same CURVE
          // over its own node rects rather than a pixel copy of what is on screen.
          const q1={x:a.x+a.w,y:a.y+a.h/2},q2={x:b.x,y:b.y+b.h/2};
          const tg=Math.min(100,Math.max(25,Math.abs(q2.x-q1.x)*0.5));
          d=`M ${q1.x} ${q1.y} C ${q1.x+tg} ${q1.y}, ${q2.x-tg} ${q2.y}, ${q2.x} ${q2.y}`;
          // No arrowhead: the canvas draws these as plain connection lines, and so do the PNG and SVG
          // exports. Direction is already unambiguous - a blend tree always flows parent-right into
          // child-left. An empty `att` is what marks an edge as headless; see the marker swaps below.
          att='';
          // On the curve, not beside it: horizontal tangents put t=0.5 exactly on the chord midpoint.
          mx=(q1.x+q2.x)/2;my=(q1.y+q2.y)/2;wx=mx;wy=my-12;
        }
        else d=`M ${p1.x} ${p1.y} L ${mx} ${my} L ${p2.x} ${p2.y}`;
        // The "-> Layer" tag, at the same 0.72 along the connection the canvas and the other exports use.
        if(ges.some(x=>x.tol)){const tx=p1.x+(p2.x-p1.x)*0.72,ty=p1.y+(p2.y-p1.y)*0.72;
          tol=`<g><rect class='tolbg' x='${tx-26}' y='${ty-8}' width='52' height='15' rx='3'/><text class='toltx' x='${tx}' y='${ty+3}'>&#8594; Layer</text><title>Transitions to the layer itself</title></g>`;}}
      // One marker per drawn edge group: the first warning in it names the fault, matching how the summary
      // tooltip already reports the first transition of a bundle.
      const wed=ges.find(x=>x.warn);const wtip=wed?(wed.warnTip||''):null;
      // A headless edge gets no data-mk/data-mksel/data-att either, so the select and reset swaps below
      // have nothing to put back - all three key off data-att being present.
      const hd=att?` data-mk='${mk}' data-mksel='${mksel}' data-att='${att}' ${att}='url(#${mk})'`:'';
      e+=`<path class='edge${kind==='default'?' corange':''}${allpo?' cdim':''}${stub?' stub':''}' data-grp='${gr.gid}'${hd} d='${d}'></path><path class='edgehit' data-grp='${gr.gid}' d='${d}'><title>${esc(ges[0].summary)}${wtip?'\n\n'+esc(wtip):''}</title></path>`;
      // Broken-transition marker: the same red diamond the canvas draws, sitting just above the midpoint.
      // Non-interactive so it never steals the hover from the edge's own hit path, which is what carries
      // the reason text. NOTE: no bare double quotes anywhere in this block - it is inside a C# verbatim
      // string, and one would end it.
      if(wtip!==null)e+=`<g pointer-events='none'><path class='ewarnd' d='M ${wx} ${wy-6} L ${wx+6} ${wy} L ${wx} ${wy+6} L ${wx-6} ${wy} Z'/><text class='ewarnt' x='${wx}' y='${wy+3}'>!</text></g>`;
      if(tol)e+=tol;
      if(g.kind==='bt'&&!multi&&ges[0].label)e+=`<text class='elbl' x='${mx}' y='${my-4}'>${esc(ges[0].label)}</text>`;}
    let ns='';g.nodes.forEach(n=>{
      if(n.type==='btdirect'||n.type==='bt1d'||n.type==='bt2d'){ns+=btNodeSvg(n);return;}
      const cls='node '+n.type+(n.isDefault?' def':'')+(n.child?' haschild':'');
      // Hexagon indent from the canvas (DrawSubStateMachineShape / the SVG export): min(14, w*0.2), so a
      // narrow hexagon keeps its angled ends in proportion instead of meeting in the middle.
      //
      // The corner radius is FIXED at 6 and deliberately does NOT follow the editor's sub-state-machine
      // setting: this file travels, and it should look the same wherever it is opened rather than carrying
      // the preferences of whoever exported it. The rect nodes are the same story - a flat rx of 8.
      const hx=Math.min(14,n.w*0.2);
      const hexPts=roundPoly([[0,n.h/2],[hx,0],[n.w-hx,0],[n.w,n.h/2],[n.w-hx,n.h],[hx,n.h]],6,4);
      const shape=(n.type==='sm'||n.type==='up')?`<polygon points='${hexPts.map(q=>q[0].toFixed(2)+','+q[1].toFixed(2)).join(' ')}'/>`:`<rect width='${n.w}' height='${n.h}' rx='8'/>`;
      let inner=shape;
      // Left-edge "transitions to the layer" dot, centred on the "Ex" tag that shares its slot.
      //
      // NOT the canvas's own geometry, on either axis - this file places the tag differently and the dot
      // has to follow THAT, not the number the canvas uses:
      //
      //   x  left-edge indicators here inset by 5 where the canvas insets by 3, and an "Ex" runs about 10
      //      wide at its 9px bold font - so its centre is 10, not the canvas's 9.
      //
      //      A hexagon gets a SMALLER inset than the usual corner indent, not none and not all of it.
      //      The full 14 is for elements at a hexagon's top or bottom, where the corners are cut away, and
      //      applying it on the middle line drove the dot into the centred title. But none does not work
      //      either: the disc is 14 across, so it meets the slope well above and below the middle, and the
      //      point itself is ROUNDED (roundPoly at radius 6) which pulls the tip about 4.5px further in
      //      than the bare polygon. 15 clears the outline and still leaves a gap before a long title.
      //   y  it centres on whatever shares its line, which is not the same thing on both kinds. A state
      //      draws its "Ex" at h/2 + 3, so the dot goes there. A machine has no "Ex" at all - what sits on
      //      that line is its TITLE, drawn at h/2 and dominant-baseline:middle - so the dot goes to h/2.
      if(n.tol){const hex=(n.type==='sm'||n.type==='up');
        inner+=`<circle class='toldot' cx='${hex?15:10}' cy='${hex?n.h/2:n.h/2+3}' r='7'><title>Transitions to the layer itself</title></circle>`;}
      if(g.kind==='bt')inner+=`<rect class='wtint' id='wt${n.id}' x='2' y='2' width='${n.w-4}' height='${n.h-4}' rx='6' fill='#5bd66b' opacity='0' pointer-events='none'/>`;
      if(n.type==='state'){
        inner+=`<text class='lbl' x='${n.w/2}' y='${n.h/2-3}'>${esc(n.label)}</text>`;
        inner+= n.none?`<text class='mnone' x='${n.w/2}' y='${n.h-6}'>None</text>`:`<text class='mname' x='${n.w/2}' y='${n.h-6}'>(${esc(n.motion)})</text>`;
        const cd=n.clip?clipmap[n.clip]:null;
        if(cd){let lt,lc,tip;const empty=!cd.props.length||cd.props.every(p=>!p.keys||!p.keys.length);
          if(empty){lt='E';lc='#ff9a1a';tip='No keyframes';}else if(cd.frames<=1){lt='S';lc='#7fccff';tip='1 frame';}else{lt=cd.length.toFixed(2)+'s';lc='#d1d1d1';tip=cd.length.toFixed(3)+'s ('+cd.frames+' frames)';}
          inner+=`<text class='ind istart' x='5' y='11' fill='${lc}'>${esc(lt)}<title>${esc(tip)}</title></text>`;}
        // Blend-tree type tag. Safe to share the top-left slot with the clip length above: a state's motion
        // is either a clip or a tree, so only one of the two can ever be present.
        if(n.btag)inner+=`<text class='ind istart' x='5' y='11' fill='${n.btagc}'>${esc(n.btag)}<title>${esc(n.btagt)}</title></text>`;
        // Centre-left external marker: the state's whole tree lives in its own asset file. Its own slot,
        // so it never collides with the type tag above.
        if(n.ex)inner+=`<text class='ind istart' x='5' y='${n.h/2+3}' fill='${n.exc}'>${esc(n.ex)}<title>${esc(n.ext)}</title></text>`;
        inner+=`<text class='ind iend ${n.wd?'ion':'ioff'}' x='${n.w-5}' y='11'>WD<title>Write Defaults ${n.wd?'On':'Off'}</title></text>`;
        // Warning mark between WD and B, matching the live node's badge order. It takes B's slot and pushes
        // B along, so a state without a warning keeps the original two-badge spacing rather than a gap.
        if(n.warn)inner+=`<text class='ind iend' x='${n.w-26}' y='11' fill='${n.warnc}'>!<title>${esc(n.warnt)}</title></text>`;
        const hasB=n.behaviours&&n.behaviours.length,bxB=n.warn?n.w-40:n.w-26;
        inner+=`<text class='ind iend ${hasB?'ion':'ioff'}' x='${bxB}' y='11'>B<title>${hasB?'Has state behaviours':'No behaviours'}</title></text>`;
        let bx=n.w-5;const by=n.h-5;
        if(cd&&cd.loop){inner+=`<text class='ind iend iglyph' x='${bx}' y='${by}'>&#8635;<title>Looping clip</title></text>`;bx-=12;}
        if(n.attrs){inner+=`<text class='ind iend iglyph' x='${bx}' y='${by}'>A<title>${esc(n.attrTip||'State attributes')}</title></text>`;}
      }
      else if(n.lines&&n.lines.length){inner+=`<text class='lbl' x='${n.w/2}' y='15'>${esc(n.label)}</text>`;n.lines.forEach((ln,i)=>inner+=`<text class='sub' x='${n.w/2}' y='${30+i*13}'>${esc(ln)}</text>`);}
      else if(n.motion){inner+=`<text class='lbl' x='${n.w/2}' y='${n.h/2-5}'>${esc(n.label)}</text><text class='sub' x='${n.w/2}' y='${n.h/2+11}'>${esc(n.motion)}</text>`;}
      else inner+=`<text class='lbl' x='${n.w/2}' y='${n.h/2}'>${esc(n.label)}</text>`;
      // The identifier pill, at the canvas's own geometry: a filled pill tucked inside the right edge and
      // centred on the node's middle line, not a line of text in the corner. It was the only renderer
      // drawing this one differently.
      // Hoverable, so the <title> inside it actually reports which identifiers were found. It was built
      // pointer-events='none', which silently made the tooltip unreachable - the same way the "-> Layer"
      // tag's was. A marker whose whole job is to say "there is something hidden here" is the last one that
      // should refuse to say what.
      if(n.type==='any'&&n.idm)inner+=`<g><rect class='idbg' x='${n.w-21}' y='${n.h/2-7}' width='18' height='14' rx='3'/><text class='idtx' x='${n.w-12}' y='${n.h/2}'>ID</text><title>Identifier transitions: ${esc(n.idm)}</title></g>`;
      // A sub-state machine holds StateMachineBehaviours exactly as a state does, so it carries the same
      // B badge - and its behaviours are listed in the detail panel below like a state's. Emitted outside
      // the label chain above, the way the Any State ID pill is. Pulled in by the hexagon's 13px angled
      // end, which cuts away the square top-right corner a state's badge sits in.
      // (No double quotes anywhere in here: this whole script is a C# verbatim string.)
      // A blend-tree motion node carries the same two corner marks the live one does: the loop glyph, and
      // AAP when the clip animates animator parameters, stepping left past the loop so the two clear each
      // other - the same order and the same rule as MotionCanvasNode. Text rather than the editor's 16x16
      // icon because every badge in this export is text; a lone embedded image would be the odd one out.
      // &#8646; not &#8635;: the blend-tree canvas marks a looping motion with the left-right pair, where a
      // STATE node's loop is a circular arrow. Two different marks on purpose, so this follows the surface it
      // is a picture of.
      //
      // AAP prefers the embedded icon and falls back to the letters, the same order the live node uses. The
      // image is carried once in DATA rather than repeated per node.
      if(n.type==='clip'){let cx=n.w-5;const ccd=n.clip?clipmap[n.clip]:null;
        if(ccd&&ccd.loop){inner+=`<text class='ind iend iglyph' x='${cx}' y='11'>&#8646;<title>Looping clip</title></text>`;cx-=20;}
        if(n.aap){inner+=DATA.aapIcon
          ?`<image x='${cx-16}' y='4' width='16' height='16' href='${DATA.aapIcon}'><title>Animates animator parameters</title></image>`
          :`<text class='ind iend' x='${cx}' y='11' fill='#66d9bf'>AAP<title>Animates animator parameters</title></text>`;}}
      if(n.type==='sm'){const smB=n.behaviours&&n.behaviours.length;
        inner+=`<text class='ind iend ${smB?'ion':'ioff'}' x='${n.w-18}' y='11'>B<title>${smB?'Has state behaviours':'No behaviours'}</title></text>`;}
      ns+=`<g class='${cls}' id='ng${n.id}' data-id='${n.id}'${n.child?` data-child='${n.child}'`:''} transform='translate(${n.x},${n.y})'>${inner}</g>`;});
    vp.innerHTML=e+ns;}

  function resetEdges(){document.querySelectorAll('.edge').forEach(p=>{p.classList.remove('sel');if(p.dataset.att)p.setAttribute(p.dataset.att,'url(#'+p.dataset.mk+')');});}
  function clearSel(){resetEdges();if(selEl&&selEl.classList)selEl.classList.remove('sel');selEl=null;detail.innerHTML=HINT;hideAnim();}
  function selectEdge(gid){resetEdges();if(selEl&&selEl.classList&&selEl.classList.contains('node'))selEl.classList.remove('sel');selEl=null;const p=vp.querySelector(`.edge[data-grp='${gid}']`);if(p){p.classList.add('sel');if(p.dataset.att)p.setAttribute(p.dataset.att,'url(#'+p.dataset.mksel+')');}}
  function selectNode(el){resetEdges();if(selEl&&selEl.classList&&selEl.classList.contains('node'))selEl.classList.remove('sel');selEl=el;el.classList.add('sel');}
  function hiEdge(gid){resetEdges();const p=vp.querySelector(`.edge[data-grp='${gid}']`);if(p){p.classList.add('sel');if(p.dataset.att)p.setAttribute(p.dataset.att,'url(#'+p.dataset.mksel+')');}} // highlight without dropping the node selection

  vp.addEventListener('click',e=>{if(moved>4)return;const nd=e.target.closest('.node');if(nd){selectNode(nd);showNode(nd.dataset.id);return;}
    const eg=e.target.closest('[data-grp]');if(eg){selectEdge(eg.dataset.grp);showGroup(eg.dataset.grp);return;}clearSel();});
  vp.addEventListener('dblclick',e=>{const g=e.target.closest('.node');if(!g)return;const nd=curGraph.nodes.find(x=>x.id===g.dataset.id);if(nd&&nd.type==='up'){if(stack.length>1)popTo(stack.length-2);return;}if(g.dataset.child)enter(g.dataset.child);});

  function behHtml(bs){if(!bs||!bs.length)return '';let h='';bs.forEach(bh=>{h+=`<h3>‣ ${esc(bh.name)}</h3>`;(bh.fields||[]).forEach(ff=>{if(!ff){h+=`<div class='bdiv'></div>`;return;}const ix=ff.indexOf('|');const kk=ix<0?ff:ff.slice(0,ix),vv=ix<0?'':ff.slice(ix+1);h+=`<div class='fld'><span>${esc(kk)}</span>${fmtVal(vv)}</div>`;});});return h;}
  function showNode(id){const n=curGraph.nodes.find(x=>x.id===id);if(!n)return;
    let h=`<h2>${esc(n.label)}</h2><span class='badge'>${esc(n.type)}</span>`+(n.child?` <span class='badge'>double-click to open</span>`:'');
    h+='<ul>'+(n.details||[]).map(x=>`<li>${esc(x)}</li>`).join('')+'</ul>'+behHtml(n.behaviours);
    if(curGraph.kind==='bt'){const inc=curGraph.edges.find(x=>x.toId===n.id);if(inc){const par=curGraph.nodes.find(x=>x.id===inc.fromId);let pm='';if(par&&par.bt&&par.bt.mode==='1d')pm=' - '+esc(par.bt.px);else if(par&&par.bt&&par.bt.mode==='2d')pm=' - '+esc(par.bt.px)+' / '+esc(par.bt.py);else if(par&&par.bt&&par.bt.mode==='direct')pm=' - direct';h+=`<div class='fld'><span>Blend input${pm}</span><span class='rt'>${esc(inc.label||'-')}</span></div>`;}}
    const outs=curGraph.edges.filter(x=>x.fromId===n.id);
    if(outs.length)h+=`<h3>Out Transitions (${outs.length})</h3><div id='olist'>`+outs.map((ed,i)=>tselBtn(ed,i)).join('')+`</div><div id='odetail'></div>`;
    detail.innerHTML=h;
    if(outs.length){const show=i=>{detail.querySelectorAll('#olist .tsel').forEach((b,j)=>b.classList.toggle('sel',j===i));document.getElementById('odetail').innerHTML=edgeHtml(outs[i]);const gr=Object.keys(groupsById).map(x=>groupsById[x]).find(gg=>gg.fromId===outs[i].fromId&&gg.toId===outs[i].toId);if(gr)hiEdge(gr.gid);};
      detail.querySelectorAll('#olist .tsel').forEach(b=>b.addEventListener('click',()=>show(+b.dataset.e)));}
    if(n.clip)openAnim(n.clip);else hideAnim();}
  // Blend-tree weight visualiser: each tree node is an HTML card (foreignObject) with a coloured header and a
  // static dark body. DBTs show a draggable input Weight row on top, then one row per parameter their children
  // output (param name · slider · number field). 1D/2D trees show only their blend parameter row(s) -
  // 'Weight' · slider · number · param name. Header darkness tracks live weight (1D/Direct exact, 2D approx).
  function fmt2(v){return ''+(Math.round(v*100)/100);}
  function pdef(p,fb){const pr=DATA.parameters.find(x=>x.name===p);if(!pr)return fb;if(pr.def==='true')return 1;if(pr.def==='false')return 0;const v=parseFloat(pr.def);return isNaN(v)?fb:v;}
  function rowL(label,p,mn,mx,val,step){return `<div class='btr'><span class='btl' title='${esc(label)}'>${esc(label)}</span><input type='range' data-p='${esc(p)}' min='${mn}' max='${mx}' step='${step}' value='${val}'><input type='text' class='btnum' data-p='${esc(p)}' value='${fmt2(val)}'></div>`;}
  function rowR(p,mn,mx,val,step){const nm=p||'(none)';return `<div class='btr'><span class='btwl'>Weight</span><input type='range' data-p='${esc(p)}' min='${mn}' max='${mx}' step='${step}' value='${val}'><input type='text' class='btnum' data-p='${esc(p)}' value='${fmt2(val)}'><span class='btrn' title='${esc(nm)}'>${esc(nm)}</span></div>`;}
  function roRow(id){return `<div class='btr'><span class='btl'>Weight</span><input type='range' class='btin' id='bw${id}' min='0' max='1' step='0.001' value='0' disabled><input type='text' class='btnum' id='bwv${id}' value='0' readonly tabindex='-1'></div>`;}
  function btNodeSvg(n){const b=n.bt;let rows='';
    if(b.mode==='direct'){
      // Rows: the input weight, then one slider per distinct parameter - the children's direct params plus
      // everything the whole subtree outputs, cascading through descendant trees. Mirrors the C# row count.
      const e0=curGraph.edges.find(e2=>e2.toId===n.id&&e2.kind==='bt'),par=e0?curGraph.nodes.find(x=>x.id===e0.fromId):null;
      const seen={},pr=[];let head='';
      if(par&&par.bt&&par.bt.mode==='direct'){const c0=par.bt.children.find(cc=>cc.cid===n.id),p0=c0?c0.dp:'';
        if(p0){seen[p0]=1;head=rowL('Weight',p0,0,1,pdef(p0,0),0.01);}else head=roRow(n.id);}
      else if(par)head=roRow(n.id); // driven by a 1D/2D parent: live read-out
      const add=(p,mn,mx)=>{if(!p||seen[p])return;seen[p]=1;pr.push(rowL(p,p,mn,mx,pdef(p,mn),(mx-mn)/200||0.01));};
      const walk=cid=>{const ch=curGraph.nodes.find(x=>x.id===cid),cb=ch&&ch.bt&&ch.bt.mode?ch.bt:null;if(!cb)return;
        if(cb.mode==='1d'){const t=cb.children.map(k=>k.th);add(cb.px,t.length?Math.min(...t):0,t.length?Math.max(...t):1);}
        else if(cb.mode==='2d'){const xs=cb.children.map(k=>k.x),ys=cb.children.map(k=>k.y);
          add(cb.px,xs.length?Math.min(...xs):0,xs.length?Math.max(...xs):1);add(cb.py,ys.length?Math.min(...ys):0,ys.length?Math.max(...ys):1);}
        cb.children.forEach(k=>{if(cb.mode==='direct')add(k.dp,0,1);walk(k.cid);});};
      b.children.forEach(c=>{add(c.dp,0,1);walk(c.cid);});
      if(!par&&!seen['Weight'])head=`<div class='btr'><span class='btl'>Weight</span><input type='range' class='btroot' min='0' max='1' step='0.01' value='1'><input type='text' class='btnum btrootv' value='1'></div>`; // root master weight (a 'Weight' param row covers it otherwise)
      rows=head+pr.join('');}
    else if(b.mode==='1d'){const t=b.children.map(c=>c.th),mn=t.length?Math.min(...t):0,mx=t.length?Math.max(...t):1;rows=rowR(b.px,mn,mx,pdef(b.px,mn),(mx-mn)/200||0.01);}
    else{const xs=b.children.map(c=>c.x),ys=b.children.map(c=>c.y),xmn=xs.length?Math.min(...xs):0,xmx=xs.length?Math.max(...xs):1,ymn=ys.length?Math.min(...ys):0,ymx=ys.length?Math.max(...ys):1;
      rows=rowR(b.px,xmn,xmx,pdef(b.px,(xmn+xmx)/2),(xmx-xmn)/200||0.01)+rowR(b.py,ymn,ymx,pdef(b.py,(ymn+ymx)/2),(ymx-ymn)/200||0.01);}
    const tag=n.btag?`<span class='bttag' style='color:${n.btagc}' title='${esc(n.btagt)}'>${esc(n.btag)}</span>`:'';
    // Ownership tag then the broken-link mark, left to right, matching the live node where the fault mark
    // sits outermost and the ownership tag steps in beside it. Both can be present - see the C# that fills
    // them. NOTE: no bare double quotes in this block - it is inside a C# verbatim string.
    const ex=n.ex?`<span class='btex' style='color:${n.exc}' title='${esc(n.ext)}'>${esc(n.ex)}</span>`:'';
    const wrn=n.warn?`<span class='btex' style='color:${n.warnc}' title='${esc(n.warnt)}'>!</span>`:'';
    return `<g class='node ${n.type}${n.child?' haschild':''}' id='ng${n.id}' data-id='${n.id}' transform='translate(${n.x},${n.y})'><foreignObject x='0' y='0' width='${n.w}' height='${n.h}'><div class='btnode ${n.type}' id='bn${n.id}'><div class='bth'>${tag}<span class='bthname'>${esc(n.label)}</span><span class='btmarks'>${ex}${wrn}</span></div>${rows}</div></foreignObject></g>`;}
  function wireBtNodes(g){btRecompute=null;if(g.kind!=='bt')return;
    const vals={};document.querySelectorAll(`.btnode input.btnum[data-p]`).forEach(inp=>{const p=inp.dataset.p;if(!(p in vals)){const v=parseFloat(inp.value);vals[p]=isNaN(v)?0:v;}});
    const nmap={};g.nodes.forEach(n=>nmap[n.id]=n);
    function localW(b){const ch=b.children;let w=ch.map(()=>0);
      if(b.mode==='direct')ch.forEach((c,i)=>w[i]=Math.max(0,vals[c.dp]||0));
      else if(b.mode==='1d'){const v=vals[b.px]||0;const cs=ch.map((c,i)=>({i,th:c.th})).sort((a,b2)=>a.th-b2.th);if(cs.length){if(v<=cs[0].th)w[cs[0].i]=1;else if(v>=cs[cs.length-1].th)w[cs[cs.length-1].i]=1;else for(let k=1;k<cs.length;k++){if(v<=cs[k].th){const a=cs[k-1],b2=cs[k],f=(v-a.th)/((b2.th-a.th)||1);w[a.i]=1-f;w[b2.i]=f;break;}}}}
      else{const vx=vals[b.px]||0,vy=vals[b.py]||0;let ex=-1;const d=ch.map((c,i)=>{const dd=(c.x-vx)*(c.x-vx)+(c.y-vy)*(c.y-vy);if(dd<1e-6)ex=i;return dd;});if(ex>=0)w[ex]=1;else{const inv=d.map(dd=>1/dd),s=inv.reduce((a,b2)=>a+b2,0)||1;inv.forEach((v2,i)=>w[i]=v2/s);}}
      return w;}
    const DARKG=[25,25,25],LIGHTG=[110,110,110]; // motion-node min/max grays (matched to the live editor)
    const HD={btdirect:[81,40,0],bt1d:[21,43,75],bt2d:[27,48,43]},HL={btdirect:[242,140,20],bt1d:[63,128,224],bt2d:[82,145,130]}; // header min/max per tree type
    function lerp(a,b,t){return 'rgb('+Math.round(a[0]+(b[0]-a[0])*t)+','+Math.round(a[1]+(b[1]-a[1])*t)+','+Math.round(a[2]+(b[2]-a[2])*t)+')';}
    let rootW=1;
    function compute(){const W={};let root=g.nodes.find(n=>n.bt&&n.bt.mode&&!g.edges.some(e2=>e2.toId===n.id))||g.nodes.find(n=>n.bt&&n.bt.mode);
      function rec(id,wt){W[id]=(W[id]||0)+wt;const nd=nmap[id];if(nd&&nd.bt&&nd.bt.mode){const lw=localW(nd.bt);nd.bt.children.forEach((c,i)=>rec(c.cid,wt*lw[i]));}}
      if(root)rec(root.id,rootW);
      // Weight drives darkness: tree headers lerp between dark/light type colour, motion nodes lerp whole-body gray.
      // Light theme inverts the motion ramp (pale at 0 weight, strong at 1) so nodes read on the light canvas.
      const isL=document.body.classList.contains('light');
      const m0=isL?[225,225,225]:DARKG,m1=isL?[105,105,105]:LIGHTG,t0=isL?[26,26,26]:[240,240,240],t1=isL?[245,245,245]:[240,240,240];
      g.nodes.forEach(n=>{const w=Math.max(0,Math.min(1,W[n.id]||0));const bn=document.getElementById('bn'+n.id);
        if(bn){const bh=bn.querySelector('.bth');if(bh&&HD[n.type])bh.style.background=lerp(HD[n.type],HL[n.type],w);
          const s=document.getElementById('bw'+n.id),v=document.getElementById('bwv'+n.id);if(s)s.value=w;if(v)v.value=fmt2(w);}
        else{const r=document.querySelector('#ng'+n.id+' rect:not(.wtint)'),lb=document.querySelector('#ng'+n.id+' .lbl');
          if(r)r.style.fill=lerp(m0,m1,w);if(lb)lb.style.fill=lerp(t0,t1,w);}});}
    function setP(p,v){vals[p]=v;
      document.querySelectorAll('.btnode input[data-p]').forEach(o=>{if(o.dataset.p!==p)return;
        if(o.type==='range')o.value=v;else if(document.activeElement!==o)o.value=fmt2(v);});compute();}
    document.querySelectorAll('.btnode input').forEach(inp=>{inp.addEventListener('pointerdown',e=>e.stopPropagation());inp.addEventListener('mousedown',e=>e.stopPropagation());inp.addEventListener('click',e=>e.stopPropagation());inp.addEventListener('dblclick',e=>e.stopPropagation());
      if(!inp.dataset.p)return; // root/read-only weight rows are wired below
      if(inp.type==='range')inp.addEventListener('input',()=>setP(inp.dataset.p,+inp.value));
      else{inp.addEventListener('keydown',e=>{e.stopPropagation();if(e.key==='Enter')inp.blur();});
        inp.addEventListener('change',()=>{const v=parseFloat(inp.value.replace(',','.'));
          if(isNaN(v)){inp.value=fmt2(vals[inp.dataset.p]||0);return;}
          inp.value=fmt2(v);setP(inp.dataset.p,v);});}});
    const rs=document.querySelector('.btroot'),rv=document.querySelector('.btrootv'); // root tree input weight
    if(rs)rs.addEventListener('input',()=>{rootW=+rs.value;if(rv)rv.value=fmt2(rootW);compute();});
    if(rv){rv.addEventListener('keydown',e=>{e.stopPropagation();if(e.key==='Enter')rv.blur();});
      rv.addEventListener('change',()=>{const v=parseFloat(rv.value.replace(',','.'));
        if(isNaN(v)){rv.value=fmt2(rootW);return;}
        rootW=Math.max(0,Math.min(1,v));rv.value=fmt2(rootW);if(rs)rs.value=rootW;compute();});}
    btRecompute=compute; // lets the theme toggle re-tint weight colours live
    compute();}
  function tselBtn(ed,i){const sm=ed.kind==='bt'||ed.kind==='default'?'':`<span class='tsm'>Solo ${cb(ed.solo)} Mute ${cb(ed.mute)}</span>`;
    return `<button class='tsel' data-e='${i}'><span class='tss'>${esc(ed.summary)}</span>${sm}</button>`;}
  function edgeHtml(ed){
    if(ed.kind==='bt'){const par=curGraph.nodes.find(x=>x.id===ed.fromId);const m=par&&par.bt?par.bt.mode:'';
      if(m==='direct')return `<div class='fld'><span>Weight parameter</span><span class='rt'>${esc(ed.label||'-')}</span></div>`;
      const pm=m==='2d'?(esc(par.bt.px)+' / '+esc(par.bt.py)):(par&&par.bt?esc(par.bt.px):'-');
      return `<div class='fld'><span>Weight parameter</span><span class='rt'>${pm}</span></div><div class='fld'><span>${m==='2d'?'Position':'Threshold'}</span><span class='rt'>${esc(ed.label||'-')}</span></div>`;}
    let h='';
    // The real endpoints, ahead of every other field. The heading above shows the two NODES the arrow
    // was drawn between, which is what you clicked; these are the states the transition actually names,
    // and the two part company as soon as a sub-state machine sits between them.
    if(ed.srcPath||ed.dstPath)h+=`<div class='fld'><span>From</span><span class='rt'>${esc(ed.srcPath||'-')}</span></div>`
      +`<div class='fld'><span>To</span><span class='rt'>${esc(ed.dstPath||'-')}</span></div>`;
    if(ed.isStateTr){h+=`<div class='fld'><span>Has Exit Time</span>${cb(ed.hasExit)}</div><details class='setd'><summary>Settings</summary>`
      +`<div class='fld'><span>Exit Time</span>${nf(ed.exitTime)}</div><div class='fld'><span>Fixed Duration</span>${cb(ed.fixedDur)}</div>`
      +`<div class='fld'><span>Transition Duration</span>${nf(ed.duration)}</div><div class='fld'><span>Transition Offset</span>${nf(ed.offset)}</div>`
      +`<div class='fld'><span>Interruption Source</span><span class='rt'>${esc(ed.interrupt)}</span></div><div class='fld'><span>Ordered Interruption</span>${cb(ed.ordered)}</div></details>`;}
    h+=`<h3>Conditions</h3>`;if(ed.conds&&ed.conds.length)h+=ed.conds.map(c=>`<div class='cond'><span class='cp'>${esc(c.param)}</span>${c.isBool?cb(c.boolVal):`<span class='cop'>${esc(c.op)}</span>${nf(c.value)}`}</div>`).join('');else h+=`<div class='hint'>No conditions</div>`;return h;}
  // Last segment of a path: SSM / SSM2 / Goal -> Goal. The heading wants the name; the From / To rows
  // below it carry the path that says which one.
  function leafName(p){if(!p)return '';const i=p.lastIndexOf(' / ');return i<0?p:p.slice(i+3);}
  function showGroup(gid){hideAnim();const gr=groupsById[gid];if(!gr)return;const eds=gr.idx.map(i=>curGraph.edges[i]);const nm={};curGraph.nodes.forEach(n=>nm[n.id]=n);
    // Fallback only. The heading names the transition's REAL ends, not the two nodes the arrow was drawn
    // between - an arrow into a sub-state machine is drawn to the machine, but the transition goes to a
    // state inside it, and reading Entry -> SSM when it is really Entry -> Goal is the whole complaint.
    // These node labels stand in for edges that have no real pair to show, which is the blend-tree graphs.
    const from=nm[gr.fromId]?nm[gr.fromId].label:gr.fromId,to=nm[gr.toId]?nm[gr.toId].label:gr.toId;
    let h=`<h2 id='thead'></h2><div class='hint'>${eds.length} transition${eds.length>1?'s':''}</div>`;
    h+=`<div>`+eds.map((ed,i)=>tselBtn(ed,i)).join('')+`</div>`;
    h+=`<div id='tdetail'></div>`;detail.innerHTML=h;
    // Re-read per selection, because one drawn edge can carry several transitions and they need not share
    // a real destination - three arrows bundled into a sub-state machine can each land on a different state.
    const show=i=>{detail.querySelectorAll('.tsel').forEach((b,j)=>b.classList.toggle('sel',j===i));
      const ed=eds[i],hd=document.getElementById('thead');
      if(hd)hd.textContent=(leafName(ed.srcPath)||from)+' → '+(leafName(ed.dstPath)||to);
      document.getElementById('tdetail').innerHTML=edgeHtml(ed);};
    detail.querySelectorAll('.tsel').forEach(b=>b.addEventListener('click',()=>show(+b.dataset.e)));show(0);}
  function showParam(p){hideAnim();let h=`<h2>${esc(p.name)}</h2><div class='fld'><span>Type</span><span class='tb tb-${p.type.toLowerCase()}'>${ABBR[p.type]||'?'}</span></div>`;
    if(p.vrc)h+=`<div class='fld'><span>VRChat built-in</span>${cb(true)}</div>`;if(p.mismatch)h+=`<div class='fld'><span>Type mismatch</span><span class='rt' style='color:#e07070'>yes</span></div>`;
    h+=`<div class='fld'><span>Default</span>${p.type==='Bool'?cb(p.def==='true'):(p.type==='Trigger'?`<span class='rt'>-</span>`:nf(p.def))}</div>`;detail.innerHTML=h;}

  function openAnim(id){const cd=clipmap[id];if(!cd)return;animtitle.textContent='Animation - '+cd.name;animwin.style.display='flex';mountDope(cd);}
  function mountDope(cd){const host=animbody;const dur=cd.length||0;const Lv=dur>0?dur*1.06:1;
    function evalC(ks,t){if(!ks.length)return 0;if(t<=ks[0].t)return ks[0].v;const n=ks.length;if(t>=ks[n-1].t)return ks[n-1].v;
      for(let i=1;i<n;i++){if(t<=ks[i].t){const a=ks[i-1],b=ks[i];if(a.co)return a.v;const dt=(b.t-a.t)||1e-6,u=(t-a.t)/dt,u2=u*u,u3=u2*u;return (2*u3-3*u2+1)*a.v+(u3-2*u2+u)*(a.outT*dt)+(-2*u3+3*u2)*b.v+(u3-u2)*(b.inT*dt);}}return ks[n-1].v;}
    function step(){const t=Lv/8,S=[0.05,0.1,0.2,0.25,0.5,1,2,5,10,20,30,60];for(const s of S)if(s>=t)return s;return 60;}
    let h=`<div class='chead'>${cd.frames} frame${cd.frames===1?'':'s'} · ${num(cd.fps)} fps · ${num(dur)}s · ${cd.props.length} properties</div>`;
    h+=`<div class='scrub'><input id='pl' type='range' min='0' max='${dur}' step='${1/(cd.fps||30)}' value='0'><span id='pll'></span></div>`;
    h+=`<div class='dtl' id='dtl' style='--nw:150px'><div class='rrow'><div class='rname'></div><div class='rname'></div><div class='rtrack' id='rtrack'></div></div>`;
    cd.props.forEach((p,i)=>{h+=`<div class='drow'><div class='dname' data-i='${i}'><span class='exar'>▸</span><span>${esc(p.path?p.path+' : ':'')}${esc(p.prop)}</span></div><div class='dval' id='pv${i}'></div><div class='dtrack' data-i='${i}'>${p.keys.map(k=>`<span class='diamond' style='left:${(k.t/Lv)*100}%'></span>`).join('')}</div></div><div class='dexp' id='dexp${i}' style='display:none'></div>`;});
    h+=`<div class='playoverlay' id='povl'><div class='playhead' id='ph'></div></div><div class='namrz' id='namrz' style='left:150px'></div></div>`;host.innerHTML=h;
    (function(){const dtl=host.querySelector('#dtl'),nr=host.querySelector('#namrz');
      let sv;try{sv=parseInt(localStorage.getItem('atk_dope_nw'));}catch(e){}if(sv){dtl.style.setProperty('--nw',sv+'px');nr.style.left=sv+'px';}
      nr.addEventListener('mousedown',e=>{e.preventDefault();const r=dtl.getBoundingClientRect();
        const mm=ev=>{let w=Math.max(80,Math.min(800,ev.clientX-r.left));dtl.style.setProperty('--nw',w+'px');nr.style.left=w+'px';try{localStorage.setItem('atk_dope_nw',w);}catch(e){}};
        const mu=()=>{window.removeEventListener('mousemove',mm);window.removeEventListener('mouseup',mu);};window.addEventListener('mousemove',mm);window.addEventListener('mouseup',mu);});})();
    let marks='';for(let s=0;s<=dur+1e-6;s+=step())marks+=`<span class='rmark' style='left:${(s/Lv)*100}%'>${num(s)}s</span>`;host.querySelector('#rtrack').innerHTML=marks;
    const pl=host.querySelector('#pl'),ov=host.querySelector('#povl');
    function upd(){const t=+pl.value;host.querySelector('#pll').textContent='Frame '+Math.round(t*cd.fps)+' / '+(cd.frames-1)+'  ('+num(t)+'s)';
      cd.props.forEach((p,i)=>{const el=host.querySelector('#pv'+i);if(el)el.textContent=num(evalC(p.keys,t));});const ph=host.querySelector('#ph');if(ph)ph.style.left=((t/Lv)*100)+'%';}
    pl.addEventListener('input',upd);
    function scrubAt(cx){const r=ov.getBoundingClientRect();pl.value=Math.max(0,Math.min(dur,(cx-r.left)/r.width*Lv));upd();}
    ov.addEventListener('mousedown',e=>{e.preventDefault();scrubAt(e.clientX);const mm=ev=>scrubAt(ev.clientX);const mu=()=>{window.removeEventListener('mousemove',mm);window.removeEventListener('mouseup',mu);};window.addEventListener('mousemove',mm);window.addEventListener('mouseup',mu);});
    host.querySelectorAll('.dname').forEach(el=>el.addEventListener('click',()=>{const i=+el.dataset.i,ex=host.querySelector('#dexp'+i),ar=el.querySelector('.exar');
      if(ex.style.display==='none'){ex.innerHTML='<ul>'+cd.props[i].keys.map(k=>`<li>${num(k.t)}s : ${num(k.v)}</li>`).join('')+'</ul>';ex.style.display='block';ar.textContent='▾';}else{ex.style.display='none';ar.textContent='▸';}}));
    upd();}

  function fit(){if(!curGraph||!curGraph.nodes.length)return;let mnx=1e9,mny=1e9,mxx=-1e9,mxy=-1e9;curGraph.nodes.forEach(n=>{mnx=Math.min(mnx,n.x);mny=Math.min(mny,n.y);mxx=Math.max(mxx,n.x+n.w);mxy=Math.max(mxy,n.y+n.h);});
    const pad=55;mnx-=pad;mny-=pad;mxx+=pad;mxy+=pad;const bw=mxx-mnx,bh=mxy-mny,r=view_el.getBoundingClientRect();tf.k=Math.min(r.width/bw,r.height/bh,1.5);tf.x=(r.width-bw*tf.k)/2-mnx*tf.k;tf.y=(r.height-bh*tf.k)/2-mny*tf.k;applyTf();}
  document.getElementById('fit').addEventListener('click',fit);
  window.addEventListener('keydown',e=>{if(e.key==='f'||e.key==='F'){const a=document.activeElement;if(a&&(a.tagName==='INPUT'||a.tagName==='TEXTAREA'))return;fit();}});
  view_el.addEventListener('wheel',e=>{e.preventDefault();const r=view_el.getBoundingClientRect(),mx=e.clientX-r.left,my=e.clientY-r.top;const wx=(mx-tf.x)/tf.k,wy=(my-tf.y)/tf.k,nk=Math.min(Math.max(tf.k*Math.exp(-e.deltaY*0.0015),0.08),5);tf.k=nk;tf.x=mx-wx*nk;tf.y=my-wy*nk;applyTf();},{passive:false});
  let drag=false,lx,ly,moved=0;
  view_el.addEventListener('mousedown',e=>{if(e.button===0||e.button===1){if(e.button===1)e.preventDefault();drag=true;moved=0;lx=e.clientX;ly=e.clientY;view_el.classList.add('grab');}});
  window.addEventListener('mousemove',e=>{if(!drag)return;const dx=e.clientX-lx,dy=e.clientY-ly;moved+=Math.abs(dx)+Math.abs(dy);tf.x+=dx;tf.y+=dy;lx=e.clientX;ly=e.clientY;applyTf();});
  window.addEventListener('mouseup',()=>{drag=false;view_el.classList.remove('grab');});
  view_el.addEventListener('auxclick',e=>{if(e.button===1)e.preventDefault();});

  renderList();if(DATA.layers.length){const b=listEl.querySelector('.lrow');if(b)b.classList.add('sel');selectLayer(0);}
})();
