﻿// Copyright (C) by Housemarque, Inc.

module LockServer

open System.Collections.Generic
open Hopac
open Hopac.Job.Infixes
open Hopac.Alt.Infixes

///////////////////////////////////////////////////////////////////////////////

type Lock = Lock of int64

type Req =
 | Acquire of lock: int64 * replyCh: Ch<unit> * abortAlt: Alt<unit>
 | Release of lock: int64

type Server = {
  mutable unique: int64
  reqCh: Ch<Req>
}

///////////////////////////////////////////////////////////////////////////////

let release s (Lock lock) = Ch.give s.reqCh (Release lock)

module Alt =
  let acquire s (Lock lock) = Alt.withNack <| fun nack ->
    let replyCh = Ch.Now.create ()
    Ch.send s.reqCh (Acquire (lock, replyCh, nack)) >>%
    Ch.Alt.take replyCh

  let withLock s l xJ =
    acquire s l >=> fun () ->
    Job.tryFinallyJob xJ (release s l)

///////////////////////////////////////////////////////////////////////////////

module Now =
  let createLock s =
    Lock (System.Threading.Interlocked.Increment &s.unique)

///////////////////////////////////////////////////////////////////////////////

let start = Job.delay <| fun () ->
  let locks = Dictionary<int64, Queue<Ch<unit> * Alt<unit>>>()
  let s = {unique = 0L; reqCh = Ch.Now.create ()}
  (Job.server << Job.forever)
   (Ch.take s.reqCh >>= function
     | Acquire (lock, replyCh, abortAlt) ->
       match locks.TryGetValue lock with
        | (true, pending) ->
          pending.Enqueue (replyCh, abortAlt)
          Job.unit
        | _ ->
          Alt.select [Ch.Alt.give replyCh () >-> fun () ->
                        locks.Add (lock, Queue<_>())
                      abortAlt]
     | Release lock ->
       match locks.TryGetValue lock with
        | (true, pending) ->
          let rec assign () =
            if 0 = pending.Count then
              locks.Remove lock |> ignore
              Job.unit
            else
              let (replyCh, abortAlt) = pending.Dequeue ()
              Alt.select [Ch.Alt.give replyCh ()
                          abortAlt >=> assign]
          assign ()
        | _ ->
          // We just ignore the erroneous release request
          Job.unit) >>% s
