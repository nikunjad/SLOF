\ *****************************************************************************
\ * Copyright (c) 2004, 2011 IBM Corporation
\ * All rights reserved.
\ * This program and the accompanying materials
\ * are made available under the terms of the BSD License
\ * which accompanies this distribution, and is available at
\ * http://www.opensource.org/licenses/bsd-license.php
\ *
\ * Contributors:
\ *     IBM Corporation - initial implementation
\ ****************************************************************************/

\ PAPR PCI host bridge.

0 VALUE phb-debug?


." Populating " pwd cr

\ needed to find the right path in the device tree
: decode-unit ( addr len -- phys.lo ... phys.hi )
   2 hex-decode-unit       \ decode string
   b lshift swap           \ shift the devicenumber to the right spot
   8 lshift or             \ add the functionnumber
   \ my-bus 10 lshift or   \ add the busnumber (assume always bus 0)
   0 0 rot                 \ make phys.lo = 0 = phys.mid
;

\ needed to have the right unit address in the device tree listing
\ phys.lo=phys.mid=0 , phys.hi=config-address
: encode-unit ( phys.lo phys-mid phys.hi -- unit-str unit-len )
   nip nip                     \ forget the phys.lo and phys.mid
   dup 8 rshift 7 and swap     \ calculate function number
   B rshift 1F and             \ calculate device number
   over IF 2 ELSE nip 1 THEN   \ create string with dev#,fn# or dev# only?
   hex-encode-unit
;


0 VALUE my-puid

: setup-puid
  s" reg" get-node get-property 0= IF
    decode-64 to my-puid 2drop
  THEN
;

setup-puid

: config-b@  puid >r my-puid TO puid rtas-config-b@ r> TO puid ;
: config-w@  puid >r my-puid TO puid rtas-config-w@ r> TO puid ;
: config-l@  puid >r my-puid TO puid rtas-config-l@ r> TO puid ;

\ define the config writes
: config-b!  puid >r my-puid TO puid rtas-config-b! r> TO puid ;
: config-w!  puid >r my-puid TO puid rtas-config-w! r> TO puid ;
: config-l!  puid >r my-puid TO puid rtas-config-l! r> TO puid ;


: map-in ( phys.lo phys.mid phys.hi size -- virt )
   phb-debug? IF cr ." map-in called: " .s cr THEN
   \ Ignore the size, phys.lo and phys.mid, get BAR from config space
   drop nip nip                         ( phys.hi )
   \ Sanity check whether config address is in expected range:
   dup FF AND 10 28 WITHIN NOT IF
      cr ." phys.hi = " . cr
      ABORT" map-in with illegal config space address"
   THEN
   00FFFFFF AND                         \ Need only bus-dev-fn+register bits
   dup config-l@                        ( phys.hi' bar.lo )
   dup 7 AND 4 = IF                     \ Is it a 64-bit BAR?
      swap 4 + config-l@ lxjoin         \ Add upper part of 64-bit BAR
   ELSE
      nip
   THEN
   F NOT AND                            \ Clear indicator bits
   translate-my-address
   phb-debug? IF ." map-in done: " .s cr THEN
;

: map-out ( virt size -- )
   phb-debug? IF ." map-out called: " .s cr THEN
   2drop 
;


: dma-alloc ( size -- virt )
   phb-debug? IF cr ." dma-alloc called: " .s cr THEN
   fff + fff not and                  \ Align size to next 4k boundary
   alloc-mem
   \ alloc-mem always returns aligned memory - double check just to be sure
   dup fff and IF
      ." Warning: dma-alloc got unaligned memory!" cr
   THEN
;

: dma-free ( virt size -- )
   phb-debug? IF cr ." dma-free called: " .s cr THEN
   fff + fff not and                  \ Align size to next 4k boundary
   free-mem
;


\ Helper variables for dma-map-in and dma-map-out
0 VALUE dma-window-liobn        \ Logical I/O bus number
0 VALUE dma-window-base         \ Start address of window
0 VALUE dma-window-size         \ Size of the window

\ Read helper variables (LIOBN, DMA window base and size) from the
\ "ibm,dma-window" property. This property is unfortunately currently located
\ in the PCI device node instead of the bus node, so we've got to use the
\ "calling-child" variable here to get to the node that initiated the call.
: (init-dma-window-vars)  ( -- )
   s" ibm,dma-window" calling-child
   get-property ABORT" no dma-window property available"
   decode-int TO dma-window-liobn
   decode-64 TO dma-window-base
   decode-64 TO dma-window-size
   2drop
;

: (clear-dma-window-vars)  ( -- )
   0 TO dma-window-liobn
   0 TO dma-window-base
   0 TO dma-window-size
;

\ We assume that firmware never maps more than the whole dma-window-size
\ so we cheat by calculating the remainder of addr/windowsize instead
\ of taking care to maintain a list of assigned device addresses
: dma-virt2dev  ( virt -- devaddr )
   dma-window-size mod dma-window-base +
;

: dma-map-in  ( virt size cachable? -- devaddr )
   phb-debug? IF cr ." dma-map-in called: " .s cr THEN
   (init-dma-window-vars)
   drop                               ( virt size )
   bounds dup >r                      ( v+s virt  R: virt )
   swap fff + fff not and             \ Align end to next 4k boundary
   swap fff not and                   ( v+s' virt'  R: virt )
   ?DO
      \ ." mapping " i . cr
      dma-window-liobn                \ liobn
      i dma-virt2dev                  \ ioba
      i 3 OR                          \ Make a read- & writeable TCE
      ( liobn ioba tce  R: virt )
      hv-put-tce ABORT" H_PUT_TCE failed"
   1000 +LOOP
   r> dma-virt2dev
   (clear-dma-window-vars)
;

: dma-map-out  ( virt devaddr size -- )
   phb-debug? IF cr ." dma-map-out called: " .s cr THEN
   (init-dma-window-vars)
   nip                                ( virt size )
   bounds                             ( v+s virt )
   swap fff + fff not and             \ Align end to next 4k boundary
   swap fff not and                   ( v+s' virt' )
   ?DO
      \ ." unmapping " i . cr
      dma-window-liobn                \ liobn
      i dma-virt2dev                  \ ioba
      i                               \ Lowest bits not set => invalid TCE
      ( liobn ioba tce )
      hv-put-tce ABORT" H_PUT_TCE failed"
   1000 +LOOP
   (clear-dma-window-vars)
;

: dma-sync  ( virt devaddr size -- )
   phb-debug? IF cr ." dma-sync called: " .s cr THEN
   \ TODO: Call flush-cache or sync here?
   3drop
;


: open  true ;
: close ;


\ Scan the child nodes of the pci root node to assign bars, fixup
\ properties etc.
: setup-children
   puid >r                          \ Save old value of puid
   my-puid TO puid                  \ Set current puid
   get-node child
   BEGIN
      dup                           \ Continue as long as there are children
   WHILE
      \ ." Working on " dup node>path type cr
      \ Set child node as current node:
      dup set-node
      \ Include the PCI device functions:
      s" pci-device.fs" included
      peer                          ( next-child-phandle )
   REPEAT
   drop
   r> TO puid                       \ Restore previous puid
;

setup-children
