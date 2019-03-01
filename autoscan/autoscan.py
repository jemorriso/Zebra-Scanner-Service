# autoscan.py

# location and endpoint data has already been validated
# all that needs to be done is to connect to DB and update
# consider how I would add the functionality of multiple NIDs and locations. Eg from a file as well as just command line args
# for now just start with command line args

# add option to specify a file 
import argparse
import sys

import sqlite3
import subprocess

#attempts to connect to database
def setup_db():
	
	try:
		#conn = sqlite3.connect('/var/www/inventory.db')
		conn = sqlite3.connect('/home/jmorrison/inventory-UI-and-scripts/inventory.db')
	 	print "Connected to database"
	except sqlite3.Error as e:
		print "Error connecting to database: {}".format(e)
        exit(1)

	return conn

#takes a socket barcode and parses it into a location, socket form and voltage
def parse_socket(barcode):
	
	location_data = {
		'location'			: '',
		'location_prefix' 	: '',
		'form'				: '',
		'voltage'			: '',
		'read_date'			: datetime.now().strftime("%Y-%m-%d %H:%M:%S")
	}
	
	pillar_format = "P[NESW][\d]{4}"

	#if on a pillar - do I need others?
	if re.match(pillar_format, barcode):
		location_data['location'] = "{}{}{}{}".format(barcode[2:4],barcode[1],barcode[4:5])
		location_data['location_prefix'] = "P-"
		location_data['form'] = barcode[6:-3]
		location_data['voltage'] = barcode [-3:]

	return location_data

def wipe_socket(db, location_data):
	#when a new location is scanned, set all devices previously at that location to location = None
	db.cursor().execute("UPDATE endpoints SET location='' WHERE location_prefix = ? AND location = ?;", (location_data['location_prefix'], location_data['location']))
	db.commit()

#adds endpoint network ID and socket data to the database
def add_endpoint(db, barcode, data):
	#chop the barcode so it's just the nid
	nid = barcode[-10:]

	#leading characters of a barcode sometimes indicate device type
	product_type_prefixes = {
		'T1' : ('TC-1116', '16'),
		'T2' : ('TC-1216', '3'),
		'T3' : ('TC-1120', '39'),
		'T4' : ('TC-1120-RD', '40'),
		'T5' : ('TC-1220', '41'),
		'T6' : ('TC-1220-RD', '42'),
		'TQ' : ('PP-1316', '12'),
		'X1' : ('XR-3100', '14'),
		'TD' : ('ERT', '') #<<this one may or may not be a thing
	}

	#some barcodes have leading characters indicating product type/id, check for those
	product_name, product_id = product_type_prefixes.get(barcode[:2],('',''))
	
	query_variables = (data['location_prefix'], data['location'], data['read_date'], data['form'], data['voltage'], product_name, product_id, nid)
	
	#check if there are aleady instances of this nid in the db
	n_rows = db.cursor().execute("SELECT COUNT(*) FROM endpoints WHERE network_id=?", (nid,)).fetchone()[0]

    # TODO: if there are other nids that have the same location as something scanned, then their location should be deleted (if they are on pillars)	
    # check if there are instances of this location 


	#if the nid exists, update it, if not, insert it
	if n_rows:
		query = "UPDATE endpoints SET location_prefix = ?, location = ?, read_date = ?, socket_form = ?, voltage = ?, product_name = ?, product_id = ? WHERE network_id = ?"
	else:
		query = "INSERT INTO endpoints (location_prefix, location, read_date, socket_form, voltage, product_name, product_id, user, project, network_id) VALUES (?,?,?,?,?,?,?,?)"
	
	#run query	
	db.cursor().execute(query, query_variables)
	
	#commit or eat shit	
	db.commit()

if __name__=="__main__":
    # print("helloworld")
    # print("helloworld"[1::-1])
    # print("helloworld"[1:0:-1])
    # print("helloworld"[1:])
    # print("helloworld"[0:0:-1])
    # print("helloworld"[::-1])
    # print("helloworld"[0::-1])

    # get args
    location = sys.argv[1]
    endpoint = sys.argv[2]

    # establish db connection
    setup_db()

    # parse location into dictionary
    location_data = parse_socket(location)

    # wipe any entries for that socket
    #wipe_socket(db, location_data)	

    # add endpoint to the database at the current location
    #add_endpoint(db, endpoint, location_data)