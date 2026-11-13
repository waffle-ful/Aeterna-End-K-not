/*
   MT19937 (Mersenne Twister) in C#. It produces the same sequence as the reference C implementation
   mt19937ar.c (initialization improved 2002/2/10) by Takuji Nishimura and Makoto Matsumoto.
   http://www.math.sci.hiroshima-u.ac.jp/m-mat/MT/emt.html

   Copyright (C) 1997 - 2002, Makoto Matsumoto and Takuji Nishimura,
   All rights reserved.

   Redistribution and use in source and binary forms, with or without
   modification, are permitted provided that the following conditions
   are met:

     1. Redistributions of source code must retain the above copyright
        notice, this list of conditions and the following disclaimer.

     2. Redistributions in binary form must reproduce the above copyright
        notice, this list of conditions and the following disclaimer in the
        documentation and/or other materials provided with the distribution.

     3. The names of its contributors may not be used to endorse or promote
        products derived from this software without specific prior written
        permission.

   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
   "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
   LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
   A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE COPYRIGHT OWNER OR
   CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
   EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
   PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
   PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
   LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
   NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
   SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;

namespace EndKnot;

public class MersenneTwister : IRandom
{
    private const int N = 624;
    private const int M = 397;
    private const uint MatrixA = 0x9908b0dfU;
    private const uint UpperMask = 0x80000000U;
    private const uint LowerMask = 0x7fffffffU;

    private readonly uint[] mt = new uint[N];
    private int mti;

    public MersenneTwister() : this((int)DateTime.UtcNow.Ticks) { }
    public MersenneTwister(int seed)
    {
        // init_genrand
        mt[0] = (uint)seed;
        for (mti = 1; mti < N; mti++)
            mt[mti] = 1812433253U * (mt[mti - 1] ^ (mt[mti - 1] >> 30)) + (uint)mti;
    }

    // genrand_int32
    public uint Next()
    {
        if (mti >= N) Twist();

        uint y = mt[mti++];
        y ^= y >> 11;
        y ^= (y << 7) & 0x9d2c5680U;
        y ^= (y << 15) & 0xefc60000U;
        y ^= y >> 18;
        return y;
    }

    private void Twist()
    {
        for (int i = 0; i < N; i++)
        {
            uint y = (mt[i] & UpperMask) | (mt[(i + 1) % N] & LowerMask);
            uint v = mt[(i + M) % N] ^ (y >> 1);
            if ((y & 1) != 0) v ^= MatrixA;
            mt[i] = v;
        }
        mti = 0;
    }

    public int Next(int minValue, int maxValue)
    {
        if (minValue < 0 || maxValue < 0) throw new ArgumentOutOfRangeException("minValue and maxValue must be bigger than 0.");

        if (minValue > maxValue) throw new ArgumentException("maxValue must be bigger than minValue.");

        if (minValue == maxValue) return minValue;

        return (int)(minValue + (Next() % (maxValue - minValue)));
    }

    public int Next(int maxValue)
    {
        return Next(0, maxValue);
    }
}
