using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Stratis.Guru.Settings
{
    public class SetupSettings
    {
        public SetupSettings()
        {
            Title = "Stratis.Guru";
            Chain = "Stratis";
            NumBlocksToShow = 5;
        }

        public string Title { get; set; }

        public string Chain { get; set; }

        public string Coin { get; set; }

        public string Footer { get; set; }

        public int NumBlocksToShow { get; set; }
    }
}
