﻿using Microsoft.Maker.RemoteWiring;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Waher.Persistence.Attributes;

namespace Sensor.History
{
	[CollectionName("HourValues")]
	public class LastHour : Base
	{
	}
}
